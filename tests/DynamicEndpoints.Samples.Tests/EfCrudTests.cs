extern alias EfCrud;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace DynamicEndpoints.Samples.Tests;

/// <summary>samples/05-EfCrud.</summary>
public sealed class EfCrudTests : IAsyncLifetime
{
    private readonly SampleApp<EfCrud::Program> _app = new();
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _client = _app.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task Scaffolded_list_pages_sorts_and_filters_the_allowlisted_fields()
    {
        var page = await _client.GetFromJsonAsync<JsonObject>("/shop/products?sort=-price");
        Assert.Equal(["ROC-2", "ANV-1", "MAG-3", "HAM-4"], page!["items"]!.AsArray().Select(i => (string)i!["sku"]!));
        Assert.Equal(4, page["total"]!.GetValue<int>());
        Assert.Null(page["items"]![0]!["purchasePrice"]);
        Assert.NotNull(page["items"]![0]!["createdAt"]);

        var cheap = await _client.GetFromJsonAsync<JsonObject>("/shop/products?maxPrice=20&sort=name");
        Assert.Equal(["Giant magnet", "Hammer"], cheap!["items"]!.AsArray().Select(i => (string)i!["name"]!));

        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/shop/products?sort=stock")).StatusCode);
    }

    [Fact]
    public async Task Create_update_and_delete_go_through_the_interceptor()
    {
        var created = await _client.PostAsJsonAsync("/shop/products", new { sku = "TNT-5", name = "Dynamite", price = 9.5, stock = 100, id = 999, purchasePrice = 1 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.NotNull(created.Headers.ETag);
        var product = (await created.Content.ReadFromJsonAsync<JsonObject>())!;
        var id = product["id"]!.GetValue<int>();
        Assert.NotEqual(999, id);
        Assert.Equal(created.Headers.Location!.ToString(), $"/shop/products/{id}");
        Assert.NotNull(product["createdAt"]);

        var negative = await _client.PostAsJsonAsync("/shop/products", new { sku = "NEG-1", name = "Debt", price = -1, stock = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
        Assert.Contains("The price can't be negative.", await negative.Content.ReadAsStringAsync());

        var patched = await _client.PatchAsJsonAsync($"/shop/products/{id}", new { name = "TNT" });
        Assert.Equal("TNT", (string?)(await patched.Content.ReadFromJsonAsync<JsonObject>())!["name"]);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/shop/products/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/shop/products/{id}")).StatusCode);
    }

    [Fact]
    public async Task The_stock_endpoint_requires_a_current_etag()
    {
        var get = await _client.GetAsync("/shop/products/1");
        var etag = get.Headers.ETag!;

        Assert.Equal((HttpStatusCode)428, (await _client.PatchAsJsonAsync("/shop/products/1/stock", new { stock = 5 })).StatusCode);

        using var stale = new HttpRequestMessage(HttpMethod.Patch, "/shop/products/1/stock") { Content = JsonContent.Create(new { stock = 5 }) };
        stale.Headers.IfMatch.Add(new EntityTagHeaderValue("\"stale\""));
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await _client.SendAsync(stale)).StatusCode);

        using var current = new HttpRequestMessage(HttpMethod.Patch, "/shop/products/1/stock") { Content = JsonContent.Create(new { stock = 5 }) };
        current.Headers.IfMatch.Add(etag);
        var updated = await _client.SendAsync(current);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.NotEqual(etag, updated.Headers.ETag);
        Assert.Equal(5, (await updated.Content.ReadFromJsonAsync<JsonObject>())!["stock"]!.GetValue<int>());
    }

    [Fact]
    public async Task Hand_written_category_endpoints_hide_the_excluded_field()
    {
        var page = await _client.GetFromJsonAsync<JsonObject>("/shop/categories?q=ool");
        var tools = Assert.Single(page!["items"]!.AsArray())!;
        Assert.Equal("Tools", (string?)tools["name"]);
        Assert.Null(tools["internalNote"]);

        var entities = await _client.GetFromJsonAsync<JsonArray>("/api/admin/endpoints/crud/entities");
        Assert.Equal(["categories", "products"], entities!.Select(e => (string)e!["name"]!).Order());
    }
}
