using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace DynamicEndpoints.Tests;

public sealed class OpenApiTests
{
    [Fact]
    public async Task Security_is_attached_per_operation_and_skips_anonymous_endpoints()
    {
        await using var host = await TestHost.StartAsync(options: o =>
        {
            o.OpenApi.AddApiKey("X-Api-Key");
            o.OpenApi.AddSecurityScheme("Bearer", new JsonObject { ["type"] = "http", ["scheme"] = "bearer" },
                appliesTo: d => d.Group == "partners");
        });
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/secured").HandledBy("echo"));
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/partners").InGroup("partners").HandledBy("echo"));
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/public").AllowAnonymous().HandledBy("echo"));

        var paths = (await host.Client.GetFromJsonAsync<JsonObject>("/openapi/dynamic.json"))!["paths"]!;

        Assert.Equal(["ApiKey"], Requirements(paths["/secured"]!["get"]!));
        Assert.Equal(["ApiKey", "Bearer"], Requirements(paths["/partners"]!["get"]!));
        Assert.Null(paths["/public"]!["get"]!["security"]);
        Assert.NotNull(paths["/secured"]!["get"]!["responses"]!["401"]);
    }

    [Fact]
    public async Task Request_and_response_examples_are_documented()
    {
        await using var host = await TestHost.StartAsync(options: o =>
            o.OpenApi.AddHeader("Idempotency-Key", "Makes retries safe.", appliesTo: d => d.Method == "POST", example: "6f9619ff-8b86-d011-b42d-00cf4fc964ff"));
        await host.Manager.CreateAsync(DynamicEndpoint.Post("/orders").HandledBy("echo")
            .FromQuery("dryRun", p => p.Boolean().Example(true))
            .FromBody("sku", p => p.Required().Example("A-1"))
            .FromBody("quantity", p => p.Integer().Example(2))
            .WithResponseExample(new { id = 7, status = "accepted" }));
        await host.Manager.CreateAsync(DynamicEndpoint.Post("/explicit").HandledBy("echo")
            .FromBody("a", p => p.Example(1))
            .WithRequestExample("""{ "a": "chosen by hand" }"""));

        var paths = (await host.Client.GetFromJsonAsync<JsonObject>("/openapi/dynamic.json"))!["paths"]!;
        var orders = paths["/orders"]!["post"]!;

        Assert.Equal("""{"sku":"A-1","quantity":2}""", orders["requestBody"]!["content"]!["application/json"]!["example"]!.ToJsonString());
        Assert.Equal("accepted", orders["responses"]!["200"]!["content"]!["application/json"]!["example"]!["status"]!.GetValue<string>());

        var parameters = orders["parameters"]!.AsArray();
        Assert.True(parameters.Single(p => p!["name"]!.GetValue<string>() == "dryRun")!["example"]!.GetValue<bool>());
        Assert.Equal("6f9619ff-8b86-d011-b42d-00cf4fc964ff",
            parameters.Single(p => p!["name"]!.GetValue<string>() == "Idempotency-Key")!["example"]!.GetValue<string>());

        Assert.Equal("chosen by hand", paths["/explicit"]!["post"]!["requestBody"]!["content"]!["application/json"]!["example"]!["a"]!.GetValue<string>());
    }

    private static IEnumerable<string> Requirements(JsonNode operation) =>
        operation["security"]!.AsArray().SelectMany(r => r!.AsObject().Select(p => p.Key));
}
