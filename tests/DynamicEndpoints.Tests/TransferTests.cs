using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicEndpoints.Tests;

public sealed class TransferTests
{
    [Fact]
    public async Task Dry_runs_and_rehearsals_log_only_what_was_really_written()
    {
        var logs = new CapturingLoggerProvider();
        await using var host = await TestHost.StartAsync(configure: b => b.Services.AddSingleton<ILoggerProvider>(logs));
        var transfer = host.Services.GetRequiredService<IDynamicEndpointTransfer>();
        var file = new DynamicEndpointExport { Endpoints = [DynamicEndpoint.Get("/logged").HandledBy("echo")] };

        await transfer.ImportAsync(file, new() { DryRun = true });
        Assert.DoesNotContain(logs.Messages, m => m.StartsWith("Created dynamic endpoint"));

        await transfer.ImportAsync(file);
        Assert.Single(logs.Messages, m => m.StartsWith("Created dynamic endpoint GET /logged"));
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider, ILogger
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Enqueue(formatter(state, exception));

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task Unpublished_drafts_are_neither_exported_nor_matched_on_import()
    {
        await using var host = await TestHost.StartAsync();
        var published = await host.Manager.CreateAsync(DynamicEndpoint.Get("/published").HandledBy("echo"));
        await host.Manager.SaveDraftAsync(new DynamicEndpointDraft { Definition = published with { Description = "draft change" } });
        var draftOnly = await host.Manager.SaveDraftAsync(new DynamicEndpointDraft { Definition = DynamicEndpoint.Get("/draft-only").HandledBy("echo") });

        var export = DynamicEndpointExport.FromJson(await host.Client.GetStringAsync("/admin/endpoints/export"));
        var exported = Assert.Single(export.Endpoints);
        Assert.Equal(published.Id, exported.Id);
        Assert.Null(exported.Description);

        // Syncing the export leaves the draft alone instead of trying to delete an endpoint the store doesn't have.
        var result = await host.Services.GetRequiredService<IDynamicEndpointTransfer>().ImportAsync(export, new() { Mode = DynamicEndpointImportMode.Sync });
        Assert.True(result.Succeeded);
        Assert.DoesNotContain(result.Items, i => i.Action == DynamicEndpointImportAction.Delete);
        Assert.NotNull(await host.Manager.GetDraftAsync(draftOnly.EndpointId));
    }

    [Fact]
    public async Task Export_is_stable_sorted_and_round_trips()
    {
        await using var host = await TestHost.StartAsync();
        var b = await host.Manager.CreateAsync(DynamicEndpoint.Get("/b").HandledBy("greeting", new { greeting = "Cześć" }).Named("B"));
        var a = await host.Manager.CreateAsync(DynamicEndpoint.Post("/a/{id}").HandledBy("echo").FromRoute("id").FromBody("x", p => p.Integer()));
        await host.Manager.UpdateAsync(b with { Description = "changed" });   // revision 2 – not part of the export

        var first = await host.Client.GetStringAsync("/admin/endpoints/export");
        var second = await host.Client.GetStringAsync("/admin/endpoints/export");

        Assert.Equal(first, second);
        Assert.DoesNotContain("revision", first);
        Assert.DoesNotContain("createdAt", first);
        Assert.Contains("Cześć", first);
        Assert.EndsWith("}\n", first);
        Assert.DoesNotContain("\r", first);
        var parsed = DynamicEndpointExport.FromJson(first);
        Assert.Equal([a.Id, b.Id], parsed.Endpoints.Select(e => e.Id));
        Assert.Equal(first, parsed.ToJson());

        var selected = await host.Client.GetFromJsonAsync<JsonObject>($"/admin/endpoints/export?id={b.Id}");
        Assert.Single(selected!["endpoints"]!.AsArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync("/admin/endpoints/export?format=xml")).StatusCode);
    }

    [Fact]
    public async Task Upsert_import_creates_updates_and_leaves_unchanged_endpoints_alone()
    {
        string export;
        await using (var source = await TestHost.StartAsync())
        {
            await source.Manager.CreateAsync(DynamicEndpoint.Get("/hello/{name}").HandledBy("greeting", new { greeting = "Hi" }).FromRoute("name"));
            await source.Manager.CreateAsync(DynamicEndpoint.Get("/ping").HandledBy("echo"));
            export = await source.Client.GetStringAsync("/admin/endpoints/export");
        }

        await using var target = await TestHost.StartAsync();
        var created = await Import(target, export);
        Assert.Equal(2, created!["created"]!.GetValue<int>());
        Assert.Equal("Hi, Ola!", await target.Client.GetStringAsync("/hello/Ola"));

        var again = await Import(target, export);
        Assert.Equal(2, again!["unchanged"]!.GetValue<int>());
        Assert.False(again["hasChanges"]!.GetValue<bool>());

        var changed = export.Replace("\"Hi\"", "\"Ahoy\"", StringComparison.Ordinal);
        var updated = await Import(target, changed);
        var item = updated!["items"]!.AsArray().Single(i => i!["action"]!.GetValue<string>() == "Update")!;
        Assert.Equal(["processorConfig"], item["changes"]!.AsArray().Select(c => c!.GetValue<string>()));
        Assert.Equal("Ahoy, Ola!", await target.Client.GetStringAsync("/hello/Ola"));
        Assert.Equal(2, (await target.Manager.GetAsync(item["id"]!.GetValue<Guid>()))!.Definition.Revision);
    }

    [Fact]
    public async Task Sync_dry_run_reports_the_diff_and_sync_deletes_what_is_missing()
    {
        await using var host = await TestHost.StartAsync();
        var keep = await host.Manager.CreateAsync(DynamicEndpoint.Get("/keep").HandledBy("echo"));
        var drop = await host.Manager.CreateAsync(DynamicEndpoint.Get("/drop").HandledBy("echo"));
        var transfer = host.Services.GetRequiredService<IDynamicEndpointTransfer>();
        var file = new DynamicEndpointExport
        {
            Endpoints =
            [
                keep with { Description = "kept" },
                DynamicEndpoint.Get("/drop").HandledBy("echo").Named("replacement"),   // takes the route of the deleted endpoint
            ],
        };

        var diff = await transfer.ImportAsync(file, new DynamicEndpointImportOptions { Mode = DynamicEndpointImportMode.Sync, DryRun = true });

        Assert.True(diff.Succeeded);
        Assert.True(diff.DryRun);
        Assert.Equal(0, diff.Deleted);
        Assert.Equal(2, diff.Updated);   // no id: "/drop" is matched by method and route
        Assert.Equal(0, diff.Created);
        Assert.Equal(drop.Id, diff.Items.Single(i => i.Route == "/drop").Id);
        Assert.Equal(2, (await host.Manager.ListAsync()).Count);
        Assert.Null((await host.Manager.GetAsync(keep.Id))!.Definition.Description);

        // Without the route match, the replacement is a new endpoint and the old one is deleted first.
        var renamed = file with { Endpoints = [file.Endpoints[0], file.Endpoints[1] with { Id = Guid.NewGuid() }] };
        var synced = await transfer.ImportAsync(renamed, new DynamicEndpointImportOptions { Mode = DynamicEndpointImportMode.Sync });

        Assert.True(synced.Succeeded);
        Assert.Equal((1, 1, 1), (synced.Created, synced.Updated, synced.Deleted));
        Assert.Null(await host.Manager.GetAsync(drop.Id));
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/drop")).StatusCode);
        Assert.Equal("kept", (await host.Manager.GetAsync(keep.Id))!.Definition.Description);
    }

    [Fact]
    public async Task Invalid_imports_write_nothing_and_create_mode_skips_existing_endpoints()
    {
        await using var host = await TestHost.StartAsync();
        var existing = await host.Manager.CreateAsync(DynamicEndpoint.Get("/existing").HandledBy("echo"));
        var file = new DynamicEndpointExport
        {
            Endpoints =
            [
                DynamicEndpoint.Get("/fine").HandledBy("echo"),
                DynamicEndpoint.Get("/twice").HandledBy("echo"),
                DynamicEndpoint.Get("/Twice").HandledBy("echo"),          // conflicts with the previous one
                DynamicEndpoint.Get("/broken").HandledBy("missing-processor"),
            ],
        };

        var response = await host.Client.PostAsync("/admin/endpoints/import",
            new StringContent(file.ToJson(), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.False(result!["succeeded"]!.GetValue<bool>());
        Assert.Equal(2, result["invalid"]!.GetValue<int>());
        Assert.Single(await host.Manager.ListAsync());

        var createOnly = await host.Services.GetRequiredService<IDynamicEndpointTransfer>().ImportAsync(
            new DynamicEndpointExport { Endpoints = [existing with { Name = "ignored" }, DynamicEndpoint.Get("/new").HandledBy("echo")] },
            new DynamicEndpointImportOptions { Mode = DynamicEndpointImportMode.Create });
        Assert.Equal((1, 1), (createOnly.Created, createOnly.Skipped));
        Assert.Null((await host.Manager.GetAsync(existing.Id))!.Definition.Name);

        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsync("/admin/endpoints/import?mode=merge",
            new StringContent("[]", Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsync("/admin/endpoints/import",
            new StringContent("""{ "format": "other/v9", "endpoints": [] }""", Encoding.UTF8, "application/json"))).StatusCode);
    }

    [Fact]
    public async Task Yaml_exports_and_imports_through_the_admin_api()
    {
        await using var host = await TestHost.StartAsync(configure: b => b.AddYamlFormat());
        await host.Manager.CreateAsync(DynamicEndpoint.Post("/orders/{id}").HandledBy("greeting", new { greeting = "true" })
            .WithDescription("Line one\nLine two")
            .FromRoute("id", p => p.Integer().Range(1, 1000))
            .FromBody("code", p => p.Pattern("^[A-Z]{3}$").Example("007"))
            .FromBody("price", p => p.Number().Example(1.5)));

        var response = await host.Client.GetAsync("/admin/endpoints/export?format=yaml");
        Assert.Equal("application/yaml", response.Content.Headers.ContentType!.MediaType);
        var yaml = await response.Content.ReadAsStringAsync();
        Assert.Contains("format: dynamic-endpoints/v1", yaml);
        Assert.Contains("greeting: \"true\"", yaml);       // strings that look like other types stay strings
        Assert.Contains("example: \"007\"", yaml);
        Assert.Contains("description: |-", yaml);

        var json = await host.Client.GetStringAsync("/admin/endpoints/export");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), Yaml.DynamicEndpointsYaml.Parse(yaml)));
        Assert.Equal(yaml, Yaml.DynamicEndpointsYaml.Write(Yaml.DynamicEndpointsYaml.Parse(yaml)));

        var import = await host.Client.PostAsync("/admin/endpoints/import?dryRun=true", new StringContent(yaml, Encoding.UTF8, "application/yaml"));
        Assert.Equal(1, (await import.Content.ReadFromJsonAsync<JsonObject>())!["unchanged"]!.GetValue<int>());
    }

    private static async Task<JsonObject?> Import(TestHost host, string export, string query = "")
    {
        var response = await host.Client.PostAsync("/admin/endpoints/import" + query, new StringContent(export, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonObject>();
    }
}
