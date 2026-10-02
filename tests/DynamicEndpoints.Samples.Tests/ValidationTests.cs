extern alias Validation;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace DynamicEndpoints.Samples.Tests;

/// <summary>samples/02-Validation – every layer, error codes, the error format and Polish messages.</summary>
public sealed class ValidationTests : IAsyncLifetime
{
    private readonly SampleApp<Validation::Program> _app = new();
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _client = _app.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task Constraints_report_all_errors_with_codes_in_the_applications_format()
    {
        var response = await _client.PostAsJsonAsync("/contacts", new { email = "not-an-email", phone = "123", country = "FR" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<JsonObject>())!["error"]!;
        Assert.Equal("Validation", (string?)error["code"]);
        var details = Details(error);
        Assert.Contains(("name", "required"), details.Select(d => (d.Field, d.Code)));
        Assert.Contains(("email", "format"), details.Select(d => (d.Field, d.Code)));
        Assert.Contains(("country", "enum"), details.Select(d => (d.Field, d.Code)));
        Assert.Contains(details, d => d.Field == "phone" && d.Message == "Use the international format, e.g. +48123456789.");
    }

    [Fact]
    public async Task Valid_requests_get_the_normalized_parameters()
    {
        var accepted = await _client.PostAsJsonAsync("/contacts", new { name = "Jan Kowalski", email = "jan@example.com" });

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("PL", (string?)(await accepted.Content.ReadFromJsonAsync<JsonObject>())!["accepted"]!["country"]);
    }

    [Fact]
    public async Task Messages_follow_Accept_Language()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/contacts") { Content = JsonContent.Create(new { name = "Jan", email = "nope" }) };
        request.Headers.AcceptLanguage.ParseAdd("pl");

        var error = (await (await _client.SendAsync(request)).Content.ReadFromJsonAsync<JsonObject>())!["error"]!;

        Assert.Equal("Wystąpił co najmniej jeden błąd walidacji.", (string?)error["message"]);
        Assert.Contains(Details(error), d => d.Field == "email" && d.Message == "Musi być poprawnym adresem e-mail.");
    }

    [Fact]
    public async Task Parameter_validators_check_nip_and_iban()
    {
        var invalid = await _client.PostAsJsonAsync("/invoices", new { nip = "526-000-12-47", iban = "PL00 1234", amount = 10 });
        var details = Details((await invalid.Content.ReadFromJsonAsync<JsonObject>())!["error"]!);
        Assert.Contains(("nip", "nip.checksum"), details.Select(d => (d.Field, d.Code)));
        Assert.Contains(details, d => d.Field == "iban" && d.Message == "Invalid IBAN.");

        var valid = await _client.PostAsJsonAsync("/invoices", new { nip = "526-000-12-46", iban = "PL61 1090 1014 0000 0712 1981 2874", amount = 10 });
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
    }

    [Fact]
    public async Task Rules_and_request_validators_run_on_otherwise_valid_requests()
    {
        var checkIn = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30);

        var reversed = await _client.PostAsJsonAsync("/bookings", new { roomType = "single", guests = 2, checkIn, checkOut = checkIn.AddDays(-1) });
        var details = Details((await reversed.Content.ReadFromJsonAsync<JsonObject>())!["error"]!);
        Assert.Contains(("checkOut", "stayOrder"), details.Select(d => (d.Field, d.Code)));
        Assert.Contains(("guests", "rule"), details.Select(d => (d.Field, d.Code)));

        var tooLong = await _client.PostAsJsonAsync("/bookings", new { roomType = "double", guests = 2, checkIn, checkOut = checkIn.AddDays(20) });
        Assert.Contains(Details((await tooLong.Content.ReadFromJsonAsync<JsonObject>())!["error"]!),
            d => d.Field == "checkOut" && d.Message == "A stay cannot be longer than 14 nights.");

        var fine = await _client.PostAsJsonAsync("/bookings", new { roomType = "double", guests = 2, checkIn, checkOut = checkIn.AddDays(3) });
        Assert.Equal(HttpStatusCode.OK, fine.StatusCode);

        var expensive = await _client.PostAsJsonAsync("/orders", new { sku = "ANV-1", quantity = 1000, unitPrice = 49.9 });
        Assert.Contains(("quantity", "orderTotal"), Details((await expensive.Content.ReadFromJsonAsync<JsonObject>())!["error"]!).Select(d => (d.Field, d.Code)));
    }

    [Fact]
    public async Task Unknown_routes_and_bad_bodies_use_the_same_format()
    {
        var notFound = await _client.GetAsync("/nothing-here");
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal("NotFound", (string?)(await notFound.Content.ReadFromJsonAsync<JsonObject>())!["error"]!["code"]);

        var malformed = await _client.PostAsync("/contacts", new StringContent("{ nope", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal("InvalidBody", (string?)(await malformed.Content.ReadFromJsonAsync<JsonObject>())!["error"]!["code"]);

        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/admin/")).StatusCode);
    }

    private static List<(string Field, string Code, string Message)> Details(JsonNode error) =>
        error["details"]!.AsArray().Select(d => ((string)d!["field"]!, (string)d["code"]!, (string)d["message"]!)).ToList();
}
