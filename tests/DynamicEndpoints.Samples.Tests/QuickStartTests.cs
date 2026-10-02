extern alias QuickStart;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace DynamicEndpoints.Samples.Tests;

/// <summary>samples/01-QuickStart – the requests of its README.</summary>
public sealed class QuickStartTests
{
    [Fact]
    public async Task Seeded_endpoints_answer_and_validate()
    {
        await using var app = new SampleApp<QuickStart::Program>();
        var client = app.CreateClient();

        var hello = await client.GetFromJsonAsync<JsonObject>("/hello/Ada");
        Assert.Equal("Hello, Ada!", (string?)hello!["message"]);

        var loud = await client.GetFromJsonAsync<JsonObject>("/greetings");
        Assert.Equal("GOOD MORNING, WORLD!", (string?)loud!["message"]);

        var tooShort = await client.GetAsync("/hello/A");
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        Assert.Contains("name", (await tooShort.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!.AsObject().Select(e => e.Key));
    }

    [Fact]
    public async Task An_endpoint_created_through_the_admin_api_answers_right_away()
    {
        await using var app = new SampleApp<QuickStart::Program>();
        var client = app.CreateClient();

        var created = await client.PostAsJsonAsync("/api/admin/endpoints", new
        {
            method = "GET",
            route = "/ahoy",
            name = "Ahoy",
            processor = "greeting",
            processorConfig = new { greeting = "Ahoy" },
            parameters = new[] { new { name = "name", source = "Query", maxLength = 30 } },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var ahoy = await client.GetFromJsonAsync<JsonObject>("/ahoy?name=Jack");
        Assert.Equal("Ahoy, Jack!", (string?)ahoy!["message"]);

        var list = await client.GetFromJsonAsync<JsonArray>("/api/admin/endpoints");
        Assert.Equal(3, list!.Count);
    }

    [Theory]
    [InlineData("/admin/")]
    [InlineData("/swagger/index.html")]
    [InlineData("/openapi/dynamic.json")]
    [InlineData("/openapi/v1.json")]
    public async Task Panel_swagger_and_documents_are_served(string path)
    {
        await using var app = new SampleApp<QuickStart::Program>();

        var response = await app.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
