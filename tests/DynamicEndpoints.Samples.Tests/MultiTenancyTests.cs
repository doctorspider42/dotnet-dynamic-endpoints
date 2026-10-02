extern alias MultiTenancy;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace DynamicEndpoints.Samples.Tests;

/// <summary>samples/06-MultiTenancy – isolation of endpoints, rows, admin APIs, OpenAPI and SQL connections.</summary>
public sealed class MultiTenancyTests : IAsyncLifetime
{
    private readonly SampleApp<MultiTenancy::Program> _app = new();
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _client = _app.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task Tenants_get_their_own_endpoints_on_the_same_route()
    {
        Assert.Equal("Welcome to Acme, guest!", (string?)(await GetJsonAsync("/welcome", "acme"))!["text"]);
        Assert.Equal("Globex greets you, Ann.", (string?)(await GetJsonAsync("/welcome?name=Ann", "globex"))!["text"]);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, "/welcome", tenant: null)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/promo", "acme")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, "/promo", "globex")).StatusCode);

        // Shared endpoints answer everybody.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/status", "globex")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/status", tenant: null)).StatusCode);
    }

    [Fact]
    public async Task Shared_crud_endpoints_serve_each_tenant_its_own_rows()
    {
        var acme = await GetJsonAsync("/products?sort=-price", "acme");
        Assert.Equal(["Rocket skates", "Anvil", "Giant magnet"], acme!["items"]!.AsArray().Select(i => (string)i!["name"]!));

        var created = await SendAsync(HttpMethod.Post, "/products", "globex", new { sku = "UMB-3", name = "Umbrella", price = 12, stock = 5, tenantId = "acme" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var globex = await GetJsonAsync("/products", "globex");
        Assert.Contains("Umbrella", globex!["items"]!.AsArray().Select(i => (string)i!["name"]!));
        Assert.DoesNotContain("Umbrella", (await GetJsonAsync("/products", "acme"))!["items"]!.AsArray().Select(i => (string)i!["name"]!));

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, "/products", tenant: null)).StatusCode);
        Assert.Equal(3, (await GetJsonAsync("/countries", "globex"))!["items"]!.AsArray().Count);
    }

    [Fact]
    public async Task A_tenant_admin_api_sees_and_creates_only_its_own_endpoints()
    {
        var acme = await _client.GetFromJsonAsync<JsonArray>("/api/admin/tenants/acme/endpoints");
        Assert.Equal(["/promo", "/reports/stock", "/welcome"], acme!.Select(s => (string)s!["definition"]!["route"]!).Order());

        var created = await _client.PostAsJsonAsync("/api/admin/tenants/globex/endpoints", new
        {
            method = "GET",
            route = "/promo",
            processor = "response",
            processorConfig = new { body = new { code = "GLOBEX10" } },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("globex", (string?)(await created.Content.ReadFromJsonAsync<JsonObject>())!["tenant"]);
        Assert.Equal("GLOBEX10", (string?)(await GetJsonAsync("/promo", "globex"))!["code"]);
        Assert.Equal("ROADRUNNER", (string?)(await GetJsonAsync("/promo", "acme"))!["code"]);

        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/admin/tenants/acme/")).StatusCode);
    }

    [Fact]
    public async Task Sql_connections_are_assigned_to_tenants()
    {
        var stock = await (await SendAsync(HttpMethod.Get, "/reports/stock", "acme")).Content.ReadFromJsonAsync<JsonArray>();
        Assert.Equal(3, stock!.Count);

        object Report(string? connection) => new
        {
            method = "GET",
            route = "/reports/everything",
            processor = "sql-query",
            processorConfig = new { query = "SELECT * FROM \"Products\"", connection },
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/admin/tenants/acme/endpoints", Report(null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/admin/tenants/globex/endpoints", Report("acme-reports"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await _client.PostAsJsonAsync("/api/admin/tenants/acme/endpoints", Report("acme-reports"))).StatusCode);
    }

    [Fact]
    public async Task Every_tenant_has_its_own_openapi_document()
    {
        var acme = await _client.GetFromJsonAsync<JsonObject>("/openapi/acme/dynamic.json");
        var globex = await _client.GetFromJsonAsync<JsonObject>("/openapi/globex/dynamic.json");
        var shared = await _client.GetFromJsonAsync<JsonObject>("/openapi/dynamic.json");

        Assert.NotNull(acme!["paths"]!["/promo"]);
        Assert.Null(globex!["paths"]!["/promo"]);
        Assert.NotNull(shared!["paths"]!["/status"]);
        Assert.Null(shared["paths"]!["/welcome"]);
    }

    private async Task<JsonObject?> GetJsonAsync(string path, string tenant) =>
        await (await SendAsync(HttpMethod.Get, path, tenant)).Content.ReadFromJsonAsync<JsonObject>();

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? tenant, object? body = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        if (tenant is not null)
        {
            request.Headers.Add("X-Tenant", tenant);
        }

        return _client.SendAsync(request);
    }
}
