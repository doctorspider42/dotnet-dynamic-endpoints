using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicEndpoints.Tests;

public sealed class ErrorResponseTests
{
    // The format of an integrator API: { apiVersion, error: { code, message, details }, requestId }.
    private static IResult Custom(DynamicErrorContext context) => Results.Json(
        new
        {
            apiVersion = "1",
            error = new
            {
                code = context.Kind.ToString(),
                message = context.Title,
                details = context.Errors.Select(e => new { field = e.Key, e.Code, e.Message }),
            },
            requestId = context.RequestId,
            endpoint = context.Endpoint?.Name,
        },
        statusCode: context.StatusCode);

    private static Task<TestHost> StartAsync(Action<IDynamicEndpointsBuilder>? configure = null) => TestHost.StartAsync(
        configure: b =>
        {
            b.UseErrorResponses(Custom)
                .AddProcessor("missing", _ => Results.NotFound())
                .AddProcessor("boom", IResult (_) => throw new InvalidOperationException("secret detail"))
                .AddProcessor("teapot", _ => Results.Text("I'm a teapot", statusCode: 418));
            configure?.Invoke(b);
        },
        configureApp: app => app.UseDynamicEndpointsErrorResponses());

    [Fact]
    public async Task Validation_errors_use_the_factory()
    {
        await using var host = await StartAsync();
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/err/validate").Named("validate").HandledBy("echo").FromQuery("n", p => p.Integer().Required()));

        var response = await host.Client.GetAsync("/err/validate?n=x");
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Validation", body!["error"]!["code"]!.GetValue<string>());
        Assert.Equal("type", body["error"]!["details"]![0]!["code"]!.GetValue<string>());
        Assert.Equal("validate", body["endpoint"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(body["requestId"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Filters_still_override_the_factory()
    {
        await using var host = await StartAsync(b => b.AddFilter(onValidationFailed: c =>
        {
            c.Result = Results.StatusCode(422);
            return ValueTask.CompletedTask;
        }));
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/err/filtered").HandledBy("echo").FromQuery("n", p => p.Required()));

        Assert.Equal((HttpStatusCode)422, (await host.Client.GetAsync("/err/filtered")).StatusCode);
    }

    [Fact]
    public async Task Empty_error_responses_and_exceptions_get_the_same_format()
    {
        await using var host = await StartAsync();
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/err/missing").HandledBy("missing"));
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/err/boom").HandledBy("boom"));
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/err/teapot").HandledBy("teapot"));

        async Task<(HttpStatusCode, JsonObject?)> Get(string path, HttpMethod? method = null)
        {
            var response = await host.Client.SendAsync(new HttpRequestMessage(method ?? HttpMethod.Get, path));
            return (response.StatusCode, response.Content.Headers.ContentLength > 0 && response.Content.Headers.ContentType?.MediaType == "application/json"
                ? await response.Content.ReadFromJsonAsync<JsonObject>()
                : null);
        }

        var (noRoute, noRouteBody) = await Get("/err/nothing-here");
        Assert.Equal((HttpStatusCode.NotFound, "NotFound"), (noRoute, noRouteBody!["error"]!["code"]!.GetValue<string>()));
        Assert.Null(noRouteBody["endpoint"]);

        var (missing, missingBody) = await Get("/err/missing");
        Assert.Equal((HttpStatusCode.NotFound, "Not found"), (missing, missingBody!["error"]!["message"]!.GetValue<string>()));

        var (boom, boomBody) = await Get("/err/boom");
        Assert.Equal((HttpStatusCode.InternalServerError, "Exception"), (boom, boomBody!["error"]!["code"]!.GetValue<string>()));
        Assert.DoesNotContain("secret detail", boomBody.ToJsonString());

        var (wrongMethod, wrongMethodBody) = await Get("/err/missing", HttpMethod.Delete);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod);
        Assert.Equal("MethodNotAllowed", wrongMethodBody!["error"]!["code"]!.GetValue<string>());

        // Responses with a body are left alone.
        var teapot = await host.Client.GetAsync("/err/teapot");
        Assert.Equal("I'm a teapot", await teapot.Content.ReadAsStringAsync());

        // Static endpoints of the application are covered too.
        Assert.Equal("ok", await host.Client.GetStringAsync("/health"));
    }

    [Fact]
    public async Task The_default_factory_keeps_problem_details()
    {
        await using var host = await TestHost.StartAsync(configureApp: app => app.UseDynamicEndpointsErrorResponses());

        var response = await host.Client.GetAsync("/nothing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Not found", (await response.Content.ReadFromJsonAsync<JsonObject>())!["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task Validators_hand_parsed_values_and_items_on_to_the_processor()
    {
        var decodes = 0;
        await using var host = await TestHost.StartAsync(configure: b => b
            .AddValidator("base64", context =>
            {
                try
                {
                    var bytes = Convert.FromBase64String(context.GetValue<string>()!);
                    Interlocked.Increment(ref decodes);
                    context.SetParsedValue(bytes);
                    context.Items["checkedBy"] = "base64";
                }
                catch (FormatException)
                {
                    context.AddError("Not base64.");
                }

                return ValueTask.CompletedTask;
            }, DynamicValidatorTargets.Parameter)
            .AddProcessor("length", request => Results.Ok(new
            {
                length = request.GetParsedValue<byte[]>("document")!.Length,
                checkedBy = request.Items["checkedBy"],
                missing = request.TryGetParsedValue<byte[]>("other", out _),
            })));
        await host.Manager.CreateAsync(DynamicEndpoint.Post("/documents").HandledBy("length")
            .FromBody("document", p => p.Required().ValidatedBy("base64")));

        var response = await host.Client.PostAsJsonAsync("/documents", new { document = Convert.ToBase64String(new byte[42]) });
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();

        Assert.Equal(42, body!["length"]!.GetValue<int>());
        Assert.Equal("base64", body["checkedBy"]!.GetValue<string>());
        Assert.False(body["missing"]!.GetValue<bool>());
        Assert.Equal(1, decodes);
    }

    [Fact]
    public async Task Middleware_finds_the_endpoint_definition_on_the_http_context()
    {
        await using var host = await TestHost.StartAsync(configureApp: app => app.Use((context, next) =>
        {
            if (context.GetDynamicEndpoint() is { } endpoint)
            {
                context.Response.Headers["X-Endpoint"] = $"{endpoint.Name}@{endpoint.Revision}:{endpoint.FindParameter("id")?.Type}";
            }

            return next(context);
        }));
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/meta/{id}").Named("meta").HandledBy("echo").FromRoute("id", p => p.Integer()));

        var response = await host.Client.GetAsync("/meta/1");

        Assert.Equal("meta@1:Integer", response.Headers.GetValues("X-Endpoint").Single());
        Assert.False((await host.Client.GetAsync("/health")).Headers.Contains("X-Endpoint"));
    }
}
