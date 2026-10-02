extern alias GitOps;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicEndpoints.Samples.Tests;

/// <summary>samples/08-GitOps – endpoints.yaml through the admin API (what the CLI does): dry run, sync, export.</summary>
public sealed class GitOpsTests : IAsyncLifetime
{
    private readonly SampleApp<GitOps::Program> _app = new();
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _client = _app.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Api-Key", "ci-secret-key");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task The_dry_run_is_the_plan_and_a_sync_makes_the_server_match_the_file()
    {
        var plan = await ImportAsync("mode=sync&dryRun=true");
        var actions = plan["items"]!.AsArray().ToDictionary(e => $"{e!["method"]} {e["route"]}", e => (string)e!["action"]!);
        Assert.Equal("Create", actions["GET /health"]);
        Assert.Equal("Create", actions["POST /orders"]);
        Assert.Equal("Update", actions["GET /orders/{id}"]);
        Assert.Equal("Delete", actions["GET /legacy"]);
        Assert.Equal("pending", (string?)(await _client.GetFromJsonAsync<JsonObject>("/orders/7"))!["status"]);

        await ImportAsync("mode=sync");

        Assert.Equal("shipped", (string?)(await _client.GetFromJsonAsync<JsonObject>("/orders/7"))!["status"]);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/legacy")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await _client.PostAsJsonAsync("/orders", new { sku = "ANV-1", quantity = 2 })).StatusCode);

        var again = await ImportAsync("mode=sync&dryRun=true");
        Assert.All(again["items"]!.AsArray(), e => Assert.Equal("Unchanged", (string?)e!["action"]));
    }

    [Fact]
    public async Task Exports_are_yaml_and_the_ci_api_needs_its_key()
    {
        var yaml = await _client.GetStringAsync("/api/ci/endpoints/export?format=yaml");
        Assert.StartsWith("format: dynamic-endpoints/v1", yaml);
        Assert.Contains("route: /legacy", yaml);

        using var anonymous = _app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/ci/endpoints")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/admin/")).StatusCode);
    }

    private async Task<JsonObject> ImportAsync(string query)
    {
        var path = Path.Combine(_app.Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath, "endpoints.yaml");
        var response = await _client.PostAsync($"/api/ci/endpoints/import?{query}",
            new StringContent(await File.ReadAllTextAsync(path), Encoding.UTF8, "application/yaml"));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }
}
