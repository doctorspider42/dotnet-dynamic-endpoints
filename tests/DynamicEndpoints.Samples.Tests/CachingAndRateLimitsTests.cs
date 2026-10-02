extern alias CachingAndRateLimits;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace DynamicEndpoints.Samples.Tests;

/// <summary>samples/10-CachingAndRateLimits.</summary>
public sealed class CachingAndRateLimitsTests : IAsyncLifetime
{
    private readonly SampleApp<CachingAndRateLimits::Program> _app = new();
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _client = _app.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task Cached_responses_have_cache_control_an_etag_and_come_from_the_output_cache()
    {
        var first = await _client.GetAsync("/prices/ANV-1");
        Assert.Equal("public, max-age=60", first.Headers.CacheControl!.ToString());
        var etag = first.Headers.ETag!;
        var second = await _client.GetAsync("/prices/ANV-1");
        Assert.Equal(await CallAsync(first), await CallAsync(second));   // the processor ran once

        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/prices/ANV-1");
        conditional.Headers.IfNoneMatch.Add(etag);
        var notModified = await _client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Empty(await notModified.Content.ReadAsByteArrayAsync());

        var balance = await _client.GetAsync("/balance");
        Assert.True(balance.Headers.CacheControl!.NoStore);
        Assert.NotEqual(await CallAsync(balance), await CallAsync(await _client.GetAsync("/balance")));
    }

    [Fact]
    public async Task The_output_cache_varies_by_language()
    {
        async Task<long> Catalog(string language)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/catalog");
            request.Headers.AcceptLanguage.ParseAdd(language);
            return await CallAsync(await _client.SendAsync(request));
        }

        var polish = await Catalog("pl");
        Assert.Equal(polish, await Catalog("pl"));
        Assert.NotEqual(polish, await Catalog("en"));
    }

    [Fact]
    public async Task The_fourth_request_in_a_minute_is_a_429_from_the_error_factory()
    {
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/quotes")).StatusCode);
        }

        var rejected = await _client.GetAsync("/quotes");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.NotNull(rejected.Headers.RetryAfter);
        var body = await rejected.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("rate_limited", (string?)body!["error"]);
        Assert.Equal("Quotes (3 per minute)", (string?)body["endpoint"]);
    }

    [Fact]
    public async Task Every_api_key_has_its_own_budget()
    {
        async Task<HttpStatusCode> Search(string key)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/search?q=anvil");
            request.Headers.Add("X-Api-Key", key);
            return (await _client.SendAsync(request)).StatusCode;
        }

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.OK, await Search("key-a"));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await Search("key-a"));
        Assert.Equal(HttpStatusCode.OK, await Search("key-b"));
    }

    private static async Task<long> CallAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonObject>())!["call"]!.GetValue<long>();
}
