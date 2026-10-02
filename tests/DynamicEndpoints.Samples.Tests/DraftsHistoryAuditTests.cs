extern alias DraftsHistoryAudit;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace DynamicEndpoints.Samples.Tests;

/// <summary>samples/09-DraftsHistoryAudit – a draft that isn't live, publishing, history, a diff, a rollback, the audit log.</summary>
public sealed class DraftsHistoryAuditTests : IAsyncLifetime
{
    private const string Prices = "/api/admin/endpoints/0199c0de-0000-7000-8000-000000000001";

    private readonly SampleApp<DraftsHistoryAudit::Program> _app = new();
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _client = _app.CreateClient();
        _client.DefaultRequestHeaders.Add("X-User", "alice");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task A_draft_goes_live_when_published_and_a_rollback_brings_back_an_old_revision()
    {
        // Revision 2 is live; the draft (price 15, a currency parameter) is not.
        Assert.Equal(12, await PriceAsync("/prices/ANV-1?currency=PLN"));
        var draft = await _client.GetFromJsonAsync<JsonObject>($"{Prices}/draft");
        Assert.Equal("New price, and a currency parameter", (string?)draft!["comment"]);
        var draftDiff = await _client.GetFromJsonAsync<JsonArray>($"{Prices}/draft/diff");
        Assert.NotEmpty(draftDiff!);

        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync($"{Prices}/publish", null)).StatusCode);
        Assert.Equal(15, await PriceAsync("/prices/ANV-1"));
        Assert.Equal("PLN", (string?)(await _client.GetFromJsonAsync<JsonObject>("/prices/ANV-1?currency=PLN"))!["currency"]);

        var history = await _client.GetFromJsonAsync<JsonArray>($"{Prices}/revisions");
        Assert.Equal(["Published", "Updated", "Created"], history!.Select(r => (string)r!["kind"]!));

        var diff = await _client.GetFromJsonAsync<JsonArray>($"{Prices}/diff?from=1&to=2");
        Assert.Contains(diff!, d => (string?)d!["path"] == "parameters[sku].maxLength");

        // Revision 1's content as revision 4.
        var rolledBack = await _client.PostAsync($"{Prices}/revisions/1/rollback", null);
        Assert.Equal(4, (await rolledBack.Content.ReadFromJsonAsync<JsonObject>())!["revision"]!.GetValue<int>());
        Assert.Equal(10, await PriceAsync("/prices/ANV-1"));
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/prices/ANV-1-LONG-SKU")).StatusCode);

        var audit = await _client.GetFromJsonAsync<JsonArray>($"{Prices}/audit");
        Assert.Contains(audit!, e => (string?)e!["user"] == "alice");
    }

    [Fact]
    public async Task Scheduled_drafts_publish_themselves()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/promo")).StatusCode);

        var draft = await _client.PostAsJsonAsync("/api/admin/endpoints/drafts", new
        {
            definition = new { method = "GET", route = "/flash-sale", processor = "response", processorConfig = new { body = new { off = 50 } } },
            publishAt = DateTimeOffset.UtcNow.AddSeconds(1),
            comment = "In a second",
        });
        Assert.True(draft.IsSuccessStatusCode, await draft.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/flash-sale")).StatusCode);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        HttpStatusCode status;
        do
        {
            await Task.Delay(250);
            status = (await _client.GetAsync("/flash-sale")).StatusCode;
        }
        while (status != HttpStatusCode.OK && DateTime.UtcNow < deadline);

        Assert.Equal(HttpStatusCode.OK, status);
    }

    private async Task<decimal> PriceAsync(string path) => (await _client.GetFromJsonAsync<JsonObject>(path))!["price"]!.GetValue<decimal>();
}
