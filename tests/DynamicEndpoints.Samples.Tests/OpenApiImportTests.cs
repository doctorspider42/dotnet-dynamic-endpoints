extern alias OpenApiImport;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicEndpoints.Samples.Tests;

/// <summary>samples/07-OpenApiImport – the mock imported on start, a processor per operation and per tag, a re-import with sync.</summary>
public sealed class OpenApiImportTests : IAsyncLifetime
{
    private readonly SampleApp<OpenApiImport::Program> _app = new();
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _client = _app.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task The_mock_answers_with_the_documented_examples()
    {
        var pets = await _client.GetFromJsonAsync<JsonArray>("/pets");
        Assert.Equal(["Rex", "Tom"], pets!.Select(p => (string)p!["name"]!));

        var pet = await _client.GetFromJsonAsync<JsonObject>("/pets/7");
        Assert.Equal(7, pet!["id"]!.GetValue<int>());

        var created = await _client.PostAsJsonAsync("/pets", new { name = "Polly", species = "bird" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("Polly", (string?)(await created.Content.ReadFromJsonAsync<JsonObject>())!["name"]);

        // The constraints of the document are enforced: species is an enum, petId at least 1.
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/pets", new { name = "Nemo", species = "fish" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/pets/0")).StatusCode);

        // x-dynamic-endpoints-processor wins over mock mode.
        Assert.Equal(12, (await _client.GetFromJsonAsync<JsonObject>("/store/inventory"))!["sold"]!.GetValue<int>());

        var list = await _client.GetFromJsonAsync<JsonArray>("/api/admin/endpoints");
        Assert.All(list!, s => Assert.Equal("openapi", (string?)s!["definition"]!["origin"]!["kind"]));
    }

    [Fact]
    public async Task A_processor_per_tag_is_reported_in_the_dry_run()
    {
        var result = await ImportAsync("petstore.json", "processorByTag=store:echo&processor=echo&routePrefix=/v1&dryRun=true");

        var operations = result["operations"]!.AsArray();
        var order = operations.Single(o => (string?)o!["operationId"] == "placeOrder")!;
        Assert.Equal("echo", (string?)order["processor"]);
        Assert.Equal("Tag", (string?)order["processorSource"]);
        var inventory = operations.Single(o => (string?)o!["operationId"] == "getInventory")!;
        Assert.Equal("OperationExtension", (string?)inventory["processorSource"]);
    }

    [Fact]
    public async Task Re_importing_version_2_with_sync_updates_creates_and_deletes()
    {
        var dryRun = await ImportAsync("petstore-v2.json", "mock=true&mode=sync&enabled=true&dryRun=true");
        var actions = dryRun["operations"]!.AsArray().ToDictionary(o => (string)o!["operationId"]!, o => (string)o!["action"]!);
        Assert.Equal("Update", actions["listPets"]);
        Assert.Equal("Create", actions["listPhotos"]);
        Assert.Equal("Delete", actions["deletePet"]);
        Assert.Equal("Unchanged", actions["getPet"]);

        await ImportAsync("petstore-v2.json", "mock=true&mode=sync&enabled=true");

        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/pets/1/photos")).StatusCode);
        Assert.Contains((await _client.DeleteAsync("/pets/1")).StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/pets?species=fish")).StatusCode);
    }

    private async Task<JsonObject> ImportAsync(string file, string query)
    {
        var path = Path.Combine(_app.Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath, file);
        var response = await _client.PostAsync($"/api/admin/endpoints/import/openapi?{query}",
            new StringContent(await File.ReadAllTextAsync(path), Encoding.UTF8, "application/json"));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }
}
