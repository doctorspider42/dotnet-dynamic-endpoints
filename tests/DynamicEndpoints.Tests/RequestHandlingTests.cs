using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace DynamicEndpoints.Tests;

public sealed class RequestHandlingTests
{
    [Fact]
    public async Task Created_endpoint_is_routable_immediately_and_converts_parameter_types()
    {
        await using var host = await TestHost.StartAsync();
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/items/{id:int}")
            .HandledBy("echo")
            .FromRoute("id", p => p.Integer())
            .FromQuery("active", p => p.Boolean())
            .FromQuery("ratio", p => p.Number())
            .FromQuery("tags", p => p.ArrayOf(ParameterType.String))
            .FromQuery("page", p => p.Integer().Default(1))
            .FromHeader("tenant", p => p.BindFrom("X-Tenant").Required())
            .FromHeader("flags", p => p.ArrayOf(ParameterType.Integer)));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/items/42?active=true&ratio=0.5&tags=a&tags=b");
        request.Headers.Add("X-Tenant", "acme");
        request.Headers.Add("flags", "1, 2,3");
        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(42, body!["id"]!.GetValue<long>());
        Assert.True(body["active"]!.GetValue<bool>());
        Assert.Equal(0.5m, body["ratio"]!.GetValue<decimal>());
        Assert.Equal(["a", "b"], body["tags"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Equal(1, body["page"]!.GetValue<long>());
        Assert.Equal("acme", body["tenant"]!.GetValue<string>());
        Assert.Equal([1L, 2L, 3L], body["flags"]!.AsArray().Select(t => t!.GetValue<long>()));
    }

    [Fact]
    public async Task Invalid_request_returns_all_validation_errors_keyed_by_request_names()
    {
        await using var host = await TestHost.StartAsync();
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/search")
            .HandledBy("echo")
            .FromQuery("q", p => p.Required().MinLength(3))
            .FromQuery("limit", p => p.Integer().Range(1, 50))
            .FromQuery("sort", p => p.OneOf("asc", "desc"))
            .FromQuery("from", p => p.Date())
            .FromQuery("code", p => p.Pattern("^[A-Z]{2}$"))
            .FromHeader("tenant", p => p.BindFrom("X-Tenant").Required()));

        var response = await host.Client.GetAsync("/search?q=ab&limit=500&sort=up&from=2025-13-40&code=abc");

        var errors = await ReadErrorsAsync(response);
        Assert.Equal(new[] { "X-Tenant", "code", "from", "limit", "q", "sort" }, errors.Keys.Order(StringComparer.Ordinal).ToArray());

        var notANumber = await ReadErrorsAsync(await host.Client.GetAsync("/search?q=abc&limit=ten"));
        Assert.Contains("not a valid integer", notANumber["limit"].Single());
    }

    [Fact]
    public async Task Body_parameters_are_strictly_typed_and_get_defaults()
    {
        await using var host = await TestHost.StartAsync();
        await host.Manager.CreateAsync(DynamicEndpoint.Post("/orders")
            .HandledBy("echo")
            .FromBody("quantity", p => p.Integer().Required().Min(1))
            .FromBody("priority", p => p.Integer().Default(3))
            .FromBody("address", p => p.Object("""{ "properties": { "city": { "type": "string", "minLength": 2 } }, "required": ["city"] }""")));

        var ok = await host.Client.PostAsJsonAsync("/orders", new { quantity = 2, address = new { city = "Gdańsk" } });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = await ok.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(3, body!["priority"]!.GetValue<long>());

        var wrongTypes = await ReadErrorsAsync(await host.Client.PostAsJsonAsync("/orders", new { quantity = "2", address = new { city = "X" } }));
        Assert.Contains("quantity", wrongTypes.Keys);
        Assert.Contains("address.city", wrongTypes.Keys);

        var wrongContentType = await host.Client.PostAsync("/orders", new StringContent("quantity=2", Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongContentType.StatusCode);

        var malformed = await ReadErrorsAsync(await host.Client.PostAsync("/orders", new StringContent("{oops", Encoding.UTF8, "application/json")));
        Assert.Contains("body", malformed.Keys);

        var notAnObject = await ReadErrorsAsync(await host.Client.PostAsync("/orders", new StringContent("[1]", Encoding.UTF8, "application/json")));
        Assert.Contains("body", notAnObject.Keys);
    }

    [Fact]
    public async Task Oversized_body_is_rejected()
    {
        await using var host = await TestHost.StartAsync(options: o => o.MaxRequestBodySize = 100);
        await host.Manager.CreateAsync(DynamicEndpoint.Post("/upload").HandledBy("echo").FromBody("data"));

        var response = await host.Client.PostAsJsonAsync("/upload", new { data = new string('x', 500) });

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Theory]
    [InlineData("2026-11-01", "2026-11-05", 1, "single", HttpStatusCode.OK)]
    [InlineData("2026-11-05", "2026-11-01", 1, "single", HttpStatusCode.BadRequest)]
    [InlineData("2026-11-01", "2026-11-05", 2, "single", HttpStatusCode.BadRequest)]
    [InlineData("2026-11-01", "2026-11-05", 2, "double", HttpStatusCode.OK)]
    public async Task Business_rules_are_evaluated_with_json_logic(string checkIn, string checkOut, int guests, string room, HttpStatusCode expected)
    {
        await using var host = await TestHost.StartAsync();
        await host.Manager.CreateAsync(DynamicEndpoint.Post("/bookings")
            .HandledBy("echo")
            .FromBody("checkIn", p => p.Date().Required())
            .FromBody("checkOut", p => p.Date().Required())
            .FromBody("guests", p => p.Integer().Required())
            .FromBody("room", p => p.Required())
            .WithRule("""{ "<": [{ "var": "checkIn" }, { "var": "checkOut" }] }""", "Check-out must be after check-in.", "checkOut")
            .WithRule("""{ "or": [{ "!=": [{ "var": "room" }, "single"] }, { "==": [{ "var": "guests" }, 1] }] }""", "Single rooms fit one guest."));

        var response = await host.Client.PostAsJsonAsync("/bookings", new { checkIn, checkOut, guests, room });

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.BadRequest)
        {
            var errors = await ReadErrorsAsync(response);
            Assert.Single(errors);
            Assert.True(errors.ContainsKey("checkOut") || errors.ContainsKey("request"));
        }
    }

    [Fact]
    public async Task Typed_processor_configuration_is_used()
    {
        await using var host = await TestHost.StartAsync();
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/greet/{name}")
            .HandledBy("greeting", new { greeting = "Siema" })
            .FromRoute("name"));

        Assert.Equal("Siema, Klaudia!", await host.Client.GetStringAsync("/greet/Klaudia"));
    }

    internal static async Task<Dictionary<string, string[]>> ReadErrorsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonObject>();
        return problem!["errors"]!.AsObject().ToDictionary(e => e.Key, e => e.Value!.AsArray().Select(m => m!.GetValue<string>()).ToArray());
    }
}
