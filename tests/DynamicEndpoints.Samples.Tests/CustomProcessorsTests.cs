extern alias CustomProcessors;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace DynamicEndpoints.Samples.Tests;

/// <summary>samples/03-CustomProcessors.</summary>
public sealed class CustomProcessorsTests : IAsyncLifetime
{
    private readonly SampleApp<CustomProcessors::Program> _app = new();
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _client = _app.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task Typed_and_inline_processors_answer()
    {
        Assert.StartsWith("Hello, Ada!", await _client.GetStringAsync("/hello/Ada"));
        Assert.Equal(42, (await _client.GetFromJsonAsync<JsonObject>("/calc/sum?a=2&b=40"))!["result"]!.GetValue<decimal>());
        Assert.Equal(3, (await _client.GetFromJsonAsync<JsonObject>("/calc/average?values=1&values=2&values=6"))!["result"]!.GetValue<decimal>());
        Assert.Equal("UTC", (string?)(await _client.GetFromJsonAsync<JsonObject>("/time"))!["timeZone"]);
    }

    [Fact]
    public async Task Notes_are_stored_and_deleting_needs_the_api_key()
    {
        var created = await _client.PostAsJsonAsync("/notes", new { title = "Buy milk" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
        var note = await _client.GetFromJsonAsync<JsonObject>($"/notes/{id}");
        Assert.Equal(3, note!["priority"]!.GetValue<int>());

        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.DeleteAsync($"/notes/{id}")).StatusCode);

        using var delete = new HttpRequestMessage(HttpMethod.Delete, $"/notes/{id}");
        delete.Headers.Add("X-Api-Key", "sample-api-key");
        Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(delete)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/notes/{id}")).StatusCode);
    }

    [Fact]
    public async Task Rejected_requests_go_through_the_filter()
    {
        var response = await _client.PostAsJsonAsync("/notes", new { title = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("1", response.Headers.GetValues("X-Validation-Errors").Single());
    }

    [Fact]
    public async Task Files_are_uploaded_and_checked()
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("Contract"), "title");
        var pdf = new ByteArrayContent("%PDF-1.7 hello"u8.ToArray());
        pdf.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(pdf, "file", "contract.pdf");

        var info = await (await _client.PostAsync("/documents", form)).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("contract.pdf", (string?)info!["fileName"]);
        Assert.Equal(14, info["length"]!.GetValue<long>());

        using var wrongType = new MultipartFormDataContent();
        wrongType.Add(new StringContent("Notes"), "title");
        var text = new StringContent("plain text");
        text.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        wrongType.Add(text, "file", "notes.txt");
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync("/documents", wrongType)).StatusCode);
    }

    [Fact]
    public async Task The_validator_hands_the_parsed_csv_to_the_processor()
    {
        var summary = await (await _client.PostAsJsonAsync("/imports/csv", new { csv = "sku,quantity\nANV-1,2\nROC-2,1" }))
            .Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(2, summary!["rows"]!.GetValue<int>());
        Assert.Equal("ROC-2", (string?)summary["records"]![1]!["sku"]);

        var wrongHeader = await _client.PostAsJsonAsync("/imports/csv", new { csv = "name\nAda" });
        Assert.Equal(HttpStatusCode.BadRequest, wrongHeader.StatusCode);
    }

    [Fact]
    public async Task The_custom_management_api_publishes_endpoints()
    {
        var created = await _client.PostAsJsonAsync("/api/greetings", new { slug = "pirate", greeting = "Ahoy" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("Ahoy, Jack!", await _client.GetStringAsync("/greetings/pirate/Jack"));

        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/greetings", new { slug = "pirate", greeting = "Again" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync("/api/greetings/pirate")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/greetings/pirate/Jack")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/admin/")).StatusCode);
    }
}
