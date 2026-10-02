using DynamicEndpoints.Cli;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace DynamicEndpoints.Tests;

public sealed class CliTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("de-cli-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task Export_diff_and_push_keep_a_server_in_sync_with_a_file()
    {
        await using var source = await TestHost.StartAsync();
        await source.Manager.CreateAsync(DynamicEndpoint.Get("/hello/{name}").Named("Hello").HandledBy("greeting", new { greeting = "Hi" }).FromRoute("name"));
        await source.Manager.CreateAsync(DynamicEndpoint.Get("/ping").HandledBy("echo"));
        await using var target = await TestHost.StartAsync();
        await target.Manager.CreateAsync(DynamicEndpoint.Get("/legacy").HandledBy("echo"));
        var file = Path.Combine(_directory, "endpoints.yaml");

        var export = await RunAsync(source, "export", "-o", file);
        Assert.Equal(0, export.ExitCode);
        Assert.StartsWith("format: dynamic-endpoints/v1", await File.ReadAllTextAsync(file));

        var diff = await RunAsync(target, "diff", file);
        Assert.Equal(2, diff.ExitCode);
        Assert.Contains("+ create    GET    /hello/{name}  (Hello)", diff.Out);
        Assert.Contains("- delete    GET    /legacy", diff.Out);
        Assert.Single(await target.Manager.ListAsync());   // a diff writes nothing

        var upsert = await RunAsync(target, "push", file);
        Assert.Equal(0, upsert.ExitCode);
        Assert.Contains("Done: 2 created, 0 updated, 0 deleted", upsert.Out);
        Assert.Equal("Hi, Ada!", await target.Client.GetStringAsync("/hello/Ada"));

        var sync = await RunAsync(target, "push", file, "--sync");
        Assert.Equal(0, sync.ExitCode);
        Assert.Contains("1 deleted", sync.Out);
        Assert.Equal(0, (await RunAsync(target, "diff", file)).ExitCode);

        await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file)).Replace("greeting: Hi", "greeting: Ahoy"));
        var changed = await RunAsync(target, "diff", file);
        Assert.Equal(2, changed.ExitCode);
        Assert.Contains("~ update    GET    /hello/{name}  (Hello)  [processorConfig]", changed.Out);

        var list = await RunAsync(target, "list");
        Assert.Equal(0, list.ExitCode);
        Assert.Contains("STATUS  METHOD  ROUTE", list.Out);
        Assert.Contains("Active  GET     /hello/{name}  Hello", list.Out);
    }

    [Fact]
    public async Task Invalid_files_are_rejected_with_exit_code_1()
    {
        await using var host = await TestHost.StartAsync();
        var file = Path.Combine(_directory, "broken.json");
        await File.WriteAllTextAsync(file, new DynamicEndpointExport { Endpoints = [DynamicEndpoint.Get("/x").HandledBy("missing")] }.ToJson());

        var push = await RunAsync(host, "push", file, "--dry-run");

        Assert.Equal(1, push.ExitCode);
        Assert.Contains("! invalid   GET    /x", push.Out);
        Assert.Contains("processor: Unknown processor 'missing'", push.Out);
        Assert.Contains("Rejected – nothing was written.", push.Out);
    }

    [Fact]
    public async Task Openapi_documents_are_imported_from_files()
    {
        await using var host = await TestHost.StartAsync();
        var file = Path.Combine(_directory, "api.yaml");
        await File.WriteAllTextAsync(file, """
            openapi: 3.1.0
            info: { title: Api, version: "1" }
            paths:
              /things/{id}:
                get:
                  summary: Get thing
                  parameters:
                    - { name: id, in: path, required: true, schema: { type: integer } }
                    - { name: trace, in: cookie, schema: { type: string } }
                  responses: { "200": { description: ok } }
            """);

        var dryRun = await RunAsync(host, "import-openapi", file, "--processor", "echo", "--dry-run", "--verbose");
        Assert.Equal(0, dryRun.ExitCode);
        Assert.Contains("+ create    GET    /things/{id}", dryRun.Out);
        Assert.Contains("not mapped: cookie parameter 'trace'", dryRun.Out);
        Assert.Empty(await host.Manager.ListAsync());

        Assert.Equal(0, (await RunAsync(host, "import-openapi", file, "--processor", "echo", "--enabled")).ExitCode);
        Assert.Equal("""{"id":5}""", await host.Client.GetStringAsync("/things/5"));
    }

    [Fact]
    public async Task Credentials_usage_and_connection_errors_have_their_own_exit_codes()
    {
        await using var host = await TestHost.StartAsync(configureApp: app => app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/admin") && context.Request.Headers["X-Admin-Key"] != "s3cret")
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next(context);
        }));

        var unauthorized = await RunAsync(host, "list");
        Assert.Equal(4, unauthorized.ExitCode);
        Assert.Contains("401", unauthorized.Error);

        Assert.Equal(0, (await RunAsync(host, "list", "--api-key", "s3cret", "--api-key-header", "X-Admin-Key")).ExitCode);
        Assert.Equal(0, (await RunAsync(host, "list", "-H", "X-Admin-Key: s3cret")).ExitCode);
        Assert.Equal(0, (await RunAsync(host, ["list"], new Dictionary<string, string>
        {
            ["DYNAMIC_ENDPOINTS_API_KEY"] = "s3cret",
            ["DYNAMIC_ENDPOINTS_API_KEY_HEADER"] = "X-Admin-Key",
        })).ExitCode);

        Assert.Equal(3, (await RunAsync(host, "list", "--dryrun")).ExitCode);
        Assert.Equal(3, (await RunAsync(host, "frobnicate")).ExitCode);
        Assert.Equal(3, (await RunAsync(host, "push")).ExitCode);
        var noUrl = await CliApplication.RunAsync(["list"], new CliEnvironment { Out = new StringWriter(), Error = new StringWriter(), Variable = _ => null });
        Assert.Equal(3, noUrl);
        Assert.Equal(0, await CliApplication.RunAsync(["--help"], new CliEnvironment { Out = new StringWriter(), Error = new StringWriter() }));
    }

    private Task<(int ExitCode, string Out, string Error)> RunAsync(TestHost host, params string[] args) =>
        RunAsync(host, args, new Dictionary<string, string>());

    private static async Task<(int ExitCode, string Out, string Error)> RunAsync(TestHost host, string[] args, Dictionary<string, string> variables)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        using var handler = host.CreateHandler();
        var exitCode = await CliApplication.RunAsync([.. args, "--url", "http://localhost/admin/endpoints"], new CliEnvironment
        {
            Out = output,
            Error = error,
            Handler = handler,
            Variable = name => variables.GetValueOrDefault(name),
        });
        return (exitCode, output.ToString().Replace("\r\n", "\n"), error.ToString());
    }
}
