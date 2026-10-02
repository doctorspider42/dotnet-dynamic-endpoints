extern alias BuiltInProcessors;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace DynamicEndpoints.Samples.Tests;

/// <summary>samples/04-BuiltInProcessors – the forwards and webhooks reach the fake upstream of the same TestServer.</summary>
public sealed class BuiltInProcessorsTests : IAsyncLifetime
{
    private readonly SampleApp<BuiltInProcessors::Program> _app = new(("Upstream:BaseUrl", "http://localhost")) { ProcessorHttpToTestServer = true };
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _client = _app.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task Http_forward_maps_or_relays_the_upstream_response()
    {
        var mapped = await _client.GetFromJsonAsync<JsonObject>("/customers/42");
        Assert.Equal(42, mapped!["id"]!.GetValue<int>());
        Assert.Equal("Customer 42", (string?)mapped["name"]);
        Assert.Equal("gold", (string?)mapped["tier"]);

        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/customers/7/raw")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/customers/101/raw")).StatusCode);
    }

    [Fact]
    public async Task Webhooks_are_signed_and_retried()
    {
        var placed = await _client.PostAsJsonAsync("/orders", new { sku = "ANV-1", quantity = 2 });
        Assert.Equal(HttpStatusCode.Accepted, placed.StatusCode);

        var flaky = await (await _client.PostAsJsonAsync("/orders/flaky", new { sku = "ROC-2" })).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(2, flaky!["attempts"]!.GetValue<int>());

        var inbox = (await _client.GetFromJsonAsync<JsonArray>("/fake/webhooks"))!;
        Assert.All(inbox, w => Assert.True((bool)w!["signatureValid"]!));
        var order = inbox.Single(w => (string?)w!["payload"]!["event"] == "order.created")!;
        Assert.Equal(2, order["payload"]!["quantity"]!.GetValue<int>());
    }

    [Fact]
    public async Task Response_templates_answer_without_code()
    {
        var status = await _client.GetAsync("/status");
        Assert.Equal("true", status.Headers.GetValues("X-Mock").Single());
        Assert.Equal("ok", (string?)(await status.Content.ReadFromJsonAsync<JsonObject>())!["status"]);

        var quote = await _client.PostAsJsonAsync("/quotes", new { sku = "MAG-3", quantity = 3 });
        Assert.Equal(HttpStatusCode.Created, quote.StatusCode);
        var body = (await quote.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(3, body["quantity"]!.GetValue<int>());
        Assert.Equal("3 × MAG-3", (string?)body["text"]);

        Assert.Equal("Hello Ada, have a nice day.", await _client.GetStringAsync("/motd?name=Ada"));
    }

    [Fact]
    public async Task Sql_queries_return_rows_a_row_or_a_value()
    {
        var expensive = await _client.GetFromJsonAsync<JsonArray>("/reports/products?minPrice=20");
        Assert.Equal(["ROC-2", "ANV-1"], expensive!.Select(r => (string)r!["sku"]!));

        Assert.Equal("Giant magnet", (string?)(await _client.GetFromJsonAsync<JsonObject>("/reports/products/MAG-3"))!["name"]);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/reports/products/XYZ-9")).StatusCode);

        Assert.Equal(1785.4, (await _client.GetFromJsonAsync<JsonNode>("/reports/stock-value"))!.GetValue<double>(), 2);
    }

    [Fact]
    public async Task Forward_targets_outside_the_allowed_hosts_are_rejected_on_save()
    {
        var response = await _client.PostAsJsonAsync("/api/admin/endpoints", new
        {
            method = "GET",
            route = "/elsewhere",
            processor = "http-forward",
            processorConfig = new { url = "https://example.com/" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("is not allowed", await response.Content.ReadAsStringAsync());
    }
}
