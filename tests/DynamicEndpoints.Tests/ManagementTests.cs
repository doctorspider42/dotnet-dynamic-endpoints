using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace DynamicEndpoints.Tests;

public sealed class ManagementTests
{
    [Fact]
    public async Task Update_changes_behaviour_and_enforces_optimistic_concurrency()
    {
        await using var host = await TestHost.StartAsync();
        var created = await host.Manager.CreateAsync(DynamicEndpoint.Get("/hi/{name}").HandledBy("greeting", new { greeting = "Hello" }).FromRoute("name"));
        Assert.Equal(1, created.Version);

        var updated = await host.Manager.UpdateAsync(created with { ProcessorConfig = new JsonObject { ["greeting"] = "Cześć" } });

        Assert.Equal(2, updated.Version);
        Assert.Equal("Cześć, Ola!", await host.Client.GetStringAsync("/hi/Ola"));
        await Assert.ThrowsAsync<DynamicEndpointConcurrencyException>(() => host.Manager.UpdateAsync(created));
    }

    [Fact]
    public async Task Disable_enable_and_delete_take_effect_without_restart()
    {
        await using var host = await TestHost.StartAsync();
        var created = await host.Manager.CreateAsync(DynamicEndpoint.Get("/ping").HandledBy("echo"));
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/ping")).StatusCode);

        await host.Manager.SetEnabledAsync(created.Id, false);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/ping")).StatusCode);
        Assert.Equal(DynamicEndpointStatus.Disabled, (await host.Manager.GetAsync(created.Id))!.Status);

        await host.Manager.SetEnabledAsync(created.Id, true);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/ping")).StatusCode);

        Assert.True(await host.Manager.DeleteAsync(created.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/ping")).StatusCode);
        Assert.Empty(await host.Manager.ListAsync());
    }

    [Fact]
    public async Task Route_conflicts_and_reserved_prefixes_are_rejected()
    {
        await using var host = await TestHost.StartAsync();
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/orders/{id}").HandledBy("echo").FromRoute("id"));

        var sameShape = await Assert.ThrowsAsync<DynamicEndpointValidationException>(() =>
            host.Manager.CreateAsync(DynamicEndpoint.Get("/Orders/{orderId:int}").HandledBy("echo").FromRoute("orderId", p => p.Integer())));
        Assert.Contains("route", sameShape.Errors.Keys);

        var staticRoute = await Assert.ThrowsAsync<DynamicEndpointValidationException>(() =>
            host.Manager.CreateAsync(DynamicEndpoint.Get("/health").HandledBy("echo")));
        Assert.Contains("application endpoint", staticRoute.Errors["route"].Single());

        var reserved = await Assert.ThrowsAsync<DynamicEndpointValidationException>(() =>
            host.Manager.CreateAsync(DynamicEndpoint.Get("/internal/stuff").HandledBy("echo")));
        Assert.Contains("reserved", reserved.Errors["route"].Single());

        var admin = await Assert.ThrowsAsync<DynamicEndpointValidationException>(() =>
            host.Manager.CreateAsync(DynamicEndpoint.Post("/admin/endpoints/x").HandledBy("echo")));
        Assert.Contains("reserved", admin.Errors["route"].Single());

        // Same route, different method – fine. Disabled duplicates – fine too.
        await host.Manager.CreateAsync(DynamicEndpoint.Delete("/orders/{id}").HandledBy("echo").FromRoute("id"));
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/orders/{x}").HandledBy("echo").FromRoute("x").Disabled());
    }

    [Fact]
    public async Task Invalid_definitions_are_rejected_with_precise_errors()
    {
        await using var host = await TestHost.StartAsync();

        var result = await host.Manager.ValidateAsync(new DynamicEndpointDefinition
        {
            Method = "GET",
            Route = "/things/{id}",
            Processor = "nope",
            Parameters =
            [
                new ParameterDefinition { Name = "payload", Source = ParameterSource.Body },
                new ParameterDefinition { Name = "code", Pattern = @"(a)\1" },
                new ParameterDefinition { Name = "code" },
                new ParameterDefinition { Name = "size", Type = ParameterType.Integer, Minimum = 10, Maximum = 1 },
                new ParameterDefinition { Name = "limit", Type = ParameterType.Integer, Maximum = 5, Default = 50 },
            ],
            Rules = [new ValidationRuleDefinition { Condition = JsonNode.Parse("""{ "nonsense": [1] }"""), Message = "x" }],
            AuthorizationPolicy = "missing-policy",
        });

        Assert.False(result.IsValid);
        Assert.Contains("processor", result.Errors.Keys);
        Assert.Contains("parameters", result.Errors.Keys);            // {id} has no route parameter
        Assert.Contains("parameters[0].source", result.Errors.Keys);  // body on GET
        Assert.Contains("parameters[1].pattern", result.Errors.Keys); // backreference is not ReDoS-safe
        Assert.Contains("parameters[2].name", result.Errors.Keys);    // duplicate
        Assert.Contains("parameters[3].minimum", result.Errors.Keys);
        Assert.Contains("parameters[4].default", result.Errors.Keys); // default violates maximum
        Assert.Contains("rules[0].condition", result.Errors.Keys);
        Assert.Contains("authorizationPolicy", result.Errors.Keys);

        var badConfig = await host.Manager.ValidateAsync(DynamicEndpoint.Get("/g").HandledBy("greeting", new { greting = "typo" }));
        Assert.Contains("processorConfig", badConfig.Errors.Keys);
    }

    [Fact]
    public async Task Admin_api_supports_the_full_lifecycle()
    {
        await using var host = await TestHost.StartAsync();
        var client = host.Client;

        var processors = await client.GetFromJsonAsync<JsonArray>("/admin/endpoints/processors");
        Assert.Contains(processors!, p => p!["name"]!.GetValue<string>() == "greeting");

        var invalid = await client.PostAsJsonAsync("/admin/endpoints", new { method = "GET", route = "/x/{id}", processor = "echo" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var create = await client.PostAsJsonAsync("/admin/endpoints", new
        {
            method = "GET",
            route = "/products/{sku}",
            processor = "echo",
            parameters = new object[]
            {
                new { name = "sku", source = "Route", pattern = "^[A-Z]{3}-[0-9]{3}$" },
                new { name = "currency", source = "Query", allowedValues = new[] { "PLN", "EUR" }, @default = "PLN" },
            },
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = (await create.Content.ReadFromJsonAsync<JsonObject>())!;
        var id = created["id"]!.GetValue<Guid>();
        Assert.Equal($"/admin/endpoints/{id}", create.Headers.Location!.OriginalString);

        Assert.Equal("PLN", (await client.GetFromJsonAsync<JsonObject>("/products/ABC-123"))!["currency"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/products/abc")).StatusCode);

        var list = await client.GetFromJsonAsync<JsonArray>("/admin/endpoints");
        Assert.Equal("Active", list!.Single()!["status"]!.GetValue<string>());

        created["route"] = "/catalog/{sku}";
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/admin/endpoints/{id}", created)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/admin/endpoints/{id}", created)).StatusCode); // stale version
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/products/ABC-123")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/catalog/ABC-123")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/admin/endpoints/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/admin/endpoints/{id}")).StatusCode);
    }

    [Fact]
    public async Task OpenApi_document_describes_active_endpoints()
    {
        await using var host = await TestHost.StartAsync();
        await host.Manager.CreateAsync(DynamicEndpoint.Post("/tenants/{id:int}/orders")
            .Named("Create order")
            .InGroup("Orders")
            .HandledBy("echo")
            .FromRoute("id", p => p.Integer())
            .FromHeader("tenant", p => p.BindFrom("X-Tenant").Required())
            .FromBody("quantity", p => p.Integer().Required().Range(1, 10))
            .FromBody("note", p => p.MaxLength(100))
            .WithRule("""{ ">": [{ "var": "quantity" }, 0] }""", "Quantity must be positive."));
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/hidden").HandledBy("echo").Disabled());

        var document = await host.Client.GetFromJsonAsync<JsonObject>("/openapi/dynamic.json");

        Assert.Equal("3.1.0", document!["openapi"]!.GetValue<string>());
        var paths = document["paths"]!.AsObject();
        Assert.False(paths.ContainsKey("/hidden"));
        var operation = paths["/tenants/{id}/orders"]!["post"]!;
        Assert.Equal("CreateOrder", operation["operationId"]!.GetValue<string>());
        Assert.Equal(["Orders"], operation["tags"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Contains("Quantity must be positive.", operation["description"]!.GetValue<string>());
        var parameters = operation["parameters"]!.AsArray();
        Assert.Contains(parameters, p => p!["name"]!.GetValue<string>() == "id" && p["in"]!.GetValue<string>() == "path");
        Assert.Contains(parameters, p => p!["name"]!.GetValue<string>() == "X-Tenant" && p["in"]!.GetValue<string>() == "header");
        var body = operation["requestBody"]!["content"]!["application/json"]!["schema"]!;
        Assert.Equal(10, body["properties"]!["quantity"]!["maximum"]!.GetValue<decimal>());
        Assert.Equal(["quantity"], body["required"]!.AsArray().Select(r => r!.GetValue<string>()));
    }
}
