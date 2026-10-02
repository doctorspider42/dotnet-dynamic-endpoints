using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicEndpoints.Tests;

public sealed class SnippetTests
{
    [Fact]
    public async Task Example_request_uses_examples_defaults_and_constraints()
    {
        await using var host = await TestHost.StartAsync(options: o => o.OpenApi.AddApiKey("X-Api-Key"));
        var created = await host.Manager.CreateAsync(DynamicEndpoint.Post("/orders/{customerId}")
            .HandledBy("echo")
            .FromRoute("customerId", p => p.Example("C 1"))
            .FromQuery("tag", p => p.ArrayOf(ParameterType.String).Items(2, 5))
            .FromQuery("page", p => p.Integer().Default(1))                     // optional with default: left out
            .FromHeader("tenantId", p => p.BindFrom("X-Tenant-Id").Required().Guid())
            .FromBody("quantity", p => p.Integer().Required().Range(5, 100))
            .FromBody("email", p => p.Email())
            .FromBody("code", p => p.MinLength(8))
            .FromBody("kind", p => p.OneOf("retail", "wholesale"))
            .FromBody("address", p => p.Object("""{ "type": "object", "properties": { "city": { "type": "string" }, "zip": { "type": "integer", "minimum": 10 } } }""")));

        var snippets = await host.Client.GetFromJsonAsync<JsonObject>($"/admin/endpoints/{created.Id}/snippets?baseUrl=https://api.example.com/");
        var request = snippets!["request"]!;

        Assert.Equal("https://api.example.com/orders/C%201?tag=string&tag=string", request["url"]!.GetValue<string>());
        Assert.Equal("application/json", request["contentType"]!.GetValue<string>());
        Assert.Equal("""{"quantity":5,"email":"user@example.com","code":"stringxx","kind":"retail","address":{"city":"string","zip":10}}""",
            request["body"]!.ToJsonString());
        var headers = request["headers"]!.AsArray().ToDictionary(h => h!["name"]!.GetValue<string>(), h => h!["value"]!.GetValue<string>());
        Assert.Equal("3fa85f64-5717-4562-b3fc-2c963f66afa6", headers["X-Tenant-Id"]);
        Assert.Equal("<api-key>", headers["X-Api-Key"]);

        var curl = snippets["curl"]!.GetValue<string>();
        Assert.StartsWith("curl -X POST 'https://api.example.com/orders/C%201?tag=string&tag=string'", curl);
        Assert.Contains("-H 'X-Tenant-Id: 3fa85f64-5717-4562-b3fc-2c963f66afa6'", curl);
        Assert.Contains("--data-raw '{\"quantity\":5,", curl);

        var httpie = snippets["httpIe"]!.GetValue<string>();
        Assert.StartsWith("http POST 'https://api.example.com/orders/C%201?tag=string&tag=string'", httpie);
        Assert.Contains("'quantity:=5'", httpie);
        Assert.Contains("'email=user@example.com'", httpie);

        var csharp = snippets["cSharp"]!.GetValue<string>();
        Assert.Contains("new HttpRequestMessage(HttpMethod.Post, \"https://api.example.com/orders/C%201?tag=string&tag=string\")", csharp);
        Assert.Contains("\"quantity\": 5", csharp);
        Assert.Contains("Encoding.UTF8, \"application/json\"", csharp);
    }

    [Fact]
    public async Task Generated_example_passes_the_endpoint_validation()
    {
        await using var host = await TestHost.StartAsync();
        var created = await host.Manager.CreateAsync(DynamicEndpoint.Post("/bookings/{id}")
            .HandledBy("echo")
            .FromRoute("id", p => p.Integer().Min(3))
            .FromQuery("from", p => p.Date().Required())
            .FromBody("guests", p => p.Integer().Required().Range(2, 4))
            .FromBody("tags", p => p.ArrayOf(ParameterType.String).Items(1, 3).MaxLength(3))
            .FromBody("phone", p => p.Phone().Required()));

        var generator = host.Services.GetRequiredService<IDynamicEndpointSnippetGenerator>();
        var example = generator.CreateExample(created, "http://localhost");
        using var message = new HttpRequestMessage(HttpMethod.Post, example.Url) { Content = JsonContent.Create(example.Body) };
        var response = await host.Client.SendAsync(message);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Form_endpoints_get_multipart_snippets()
    {
        await using var host = await TestHost.StartAsync();
        var created = await host.Manager.CreateAsync(DynamicEndpoint.Post("/documents")
            .RequireAuthorization()
            .HandledBy("echo")
            .FromForm("title", p => p.Required().Example("It's mine"))
            .FromForm("document", p => p.File(1024, "application/pdf").Required()));

        var snippets = await host.Client.GetFromJsonAsync<JsonObject>($"/admin/endpoints/{created.Id}/snippets");

        Assert.Equal("multipart/form-data", snippets!["request"]!["contentType"]!.GetValue<string>());
        Assert.StartsWith("http://localhost/documents", snippets["request"]!["url"]!.GetValue<string>());
        var curl = snippets["curl"]!.GetValue<string>();
        Assert.Contains("--form-string 'title=It'\\''s mine'", curl);
        Assert.Contains("-F 'document=@document.pdf;type=application/pdf'", curl);
        Assert.Contains("-H 'Authorization: Bearer <token>'", curl);
        Assert.Contains("http --multipart POST", snippets["httpIe"]!.GetValue<string>());
        Assert.Contains("new MultipartFormDataContent()", snippets["cSharp"]!.GetValue<string>());
    }

    [Fact]
    public async Task Snippets_for_unsaved_definitions_and_errors()
    {
        await using var host = await TestHost.StartAsync();

        var preview = await host.Client.PostAsJsonAsync("/admin/endpoints/snippets?baseUrl=http://x",
            new { method = "GET", route = "/things/{id}", parameters = new[] { new { name = "id", source = "Route", type = "Integer" } } });
        Assert.Equal("http://x/things/1", (await preview.Content.ReadFromJsonAsync<JsonObject>())!["request"]!["url"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync($"/admin/endpoints/{Guid.NewGuid()}/snippets")).StatusCode);
        var created = await host.Manager.CreateAsync(DynamicEndpoint.Get("/a").HandledBy("echo"));
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync($"/admin/endpoints/{created.Id}/snippets?baseUrl=ftp://x")).StatusCode);
    }

    [Fact]
    public async Task Snippets_carry_the_tenant_in_the_route_prefix_and_the_tenant_header()
    {
        await using var host = await TestHost.StartAsync(
            configure: b => b.UseMultiTenancy(t => t.FromHeader("X-Tenant").FromRoutePrefix("/t/{tenant}")),
            configureApp: app => app.MapDynamicEndpointsTenantAdmin("/admin/tenants/{tenant}/endpoints"));
        var own = await host.Manager.CreateAsync(DynamicEndpoint.Get("/orders").HandledBy("echo").Build() with { Tenant = "acme" });
        var shared = await host.Manager.CreateAsync(DynamicEndpoint.Get("/status").HandledBy("echo"));

        async Task<JsonNode> RequestAsync(string url) => (await host.Client.GetFromJsonAsync<JsonObject>(url))!["request"]!;
        static string? Header(JsonNode request, string name) =>
            request["headers"]!.AsArray().FirstOrDefault(h => h!["name"]!.GetValue<string>() == name)?["value"]?.GetValue<string>();

        // An endpoint of a tenant always uses its own tenant.
        var request = await RequestAsync($"/admin/endpoints/{own.Id}/snippets?baseUrl=https://api.example.com&tenant=globex");
        Assert.Equal("https://api.example.com/t/acme/orders", request["url"]!.GetValue<string>());
        Assert.Equal("acme", Header(request, "X-Tenant"));

        // A shared endpoint as seen by the tenant of ?tenant=, or with the placeholder.
        request = await RequestAsync($"/admin/endpoints/{shared.Id}/snippets?baseUrl=https://api.example.com&tenant=globex");
        Assert.Equal("https://api.example.com/t/globex/status", request["url"]!.GetValue<string>());
        Assert.Equal("globex", Header(request, "X-Tenant"));
        request = await RequestAsync($"/admin/endpoints/{shared.Id}/snippets?baseUrl=https://api.example.com");
        Assert.Equal("https://api.example.com/t/{tenant}/status", request["url"]!.GetValue<string>());
        Assert.Null(Header(request, "X-Tenant"));

        // A tenant's admin API shows requests of its tenant.
        var preview = await host.Client.PostAsJsonAsync("/admin/tenants/initech/endpoints/snippets?baseUrl=http://x&tenant=globex",
            new { method = "GET", route = "/draft" });
        request = (await preview.Content.ReadFromJsonAsync<JsonObject>())!["request"]!;
        Assert.Equal("http://x/t/initech/draft", request["url"]!.GetValue<string>());
        Assert.Contains("-H 'X-Tenant: acme'", (await host.Client.GetFromJsonAsync<JsonObject>(
            $"/admin/tenants/acme/endpoints/{own.Id}/snippets"))!["curl"]!.GetValue<string>());
    }
}
