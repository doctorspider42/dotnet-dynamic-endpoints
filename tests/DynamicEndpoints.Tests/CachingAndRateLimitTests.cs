using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicEndpoints.Tests;

public sealed class CachingAndRateLimitTests
{
    [Fact]
    public async Task Cache_control_and_etags_answer_unchanged_responses_with_304()
    {
        await using var host = await TestHost.StartAsync();
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/prices/{sku}").HandledBy("echo")
            .FromRoute("sku", p => p.MaxLength(5))
            .Cached(TimeSpan.FromMinutes(1), eTag: true));

        var first = await host.Client.GetAsync("/prices/A1");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("public, max-age=60", first.Headers.CacheControl!.ToString());
        var etag = first.Headers.ETag!;
        Assert.Equal("""{"sku":"A1"}""", await first.Content.ReadAsStringAsync());

        using var revalidate = new HttpRequestMessage(HttpMethod.Get, "/prices/A1");
        revalidate.Headers.IfNoneMatch.Add(etag);
        var notModified = await host.Client.SendAsync(revalidate);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Empty(await notModified.Content.ReadAsByteArrayAsync());

        using var other = new HttpRequestMessage(HttpMethod.Get, "/prices/B2");
        other.Headers.IfNoneMatch.Add(etag);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(other)).StatusCode);

        // Errors are never cacheable.
        var invalid = await host.Client.GetAsync("/prices/TOO-LONG");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Null(invalid.Headers.CacheControl);
        Assert.Null(invalid.Headers.ETag);
    }

    [Fact]
    public async Task Etag_without_max_age_requires_revalidation_and_authorized_endpoints_are_private()
    {
        await using var host = await TestHost.StartAsync();
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/a").AllowAnonymous().HandledBy("echo")
            .WithCaching(new DynamicEndpointCaching { ETag = true }));
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/b").HandledBy("echo")
            .WithCaching(new DynamicEndpointCaching { NoStore = true }));

        Assert.Equal("public, no-cache", (await host.Client.GetAsync("/a")).Headers.CacheControl!.ToString());
        Assert.Equal("no-store", (await host.Client.GetAsync("/b")).Headers.CacheControl!.ToString());

        var result = await host.Manager.ValidateAsync(DynamicEndpoint.Get("/c").RequireAuthorization().HandledBy("echo")
            .WithCaching(new DynamicEndpointCaching { MaxAgeSeconds = 10, Visibility = CacheVisibility.Public }));
        Assert.Contains("caching.visibility", result.Errors.Keys);
        Assert.Equal("private, max-age=10", ResponseCachingHeader(DynamicEndpoint.Get("/c").RequireAuthorization().HandledBy("echo")
            .Cached(TimeSpan.FromSeconds(10))));
    }

    [Fact]
    public async Task Output_cache_serves_cached_responses_varies_by_header_parameters_and_is_evicted_on_change()
    {
        var calls = 0;
        await using var host = await TestHost.StartAsync(
            configure: b =>
            {
                b.Services.AddOutputCache();
                b.AddProcessor("counter", r => Results.Ok(new { call = Interlocked.Increment(ref calls), lang = r.Get<string>("lang") }));
            },
            configureApp: app => app.UseOutputCache());
        var created = await host.Manager.CreateAsync(DynamicEndpoint.Get("/counter").HandledBy("counter")
            .FromHeader("lang", p => p.BindFrom("X-Lang"))
            .WithCaching(new DynamicEndpointCaching { OutputCacheSeconds = 60 }));

        async Task<int> Call(string lang)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/counter");
            request.Headers.Add("X-Lang", lang);
            var body = await (await host.Client.SendAsync(request)).Content.ReadFromJsonAsync<JsonObject>();
            return body!["call"]!.GetValue<int>();
        }

        Assert.Equal(1, await Call("en"));
        Assert.Equal(1, await Call("en"));
        Assert.Equal(2, await Call("pl"));

        await host.Manager.UpdateAsync(created with { Name = "Counter" });
        Assert.Equal(3, await Call("en"));
    }

    [Fact]
    public async Task Invalid_caching_settings_are_rejected()
    {
        await using var host = await TestHost.StartAsync();

        var post = await host.Manager.ValidateAsync(DynamicEndpoint.Post("/x").HandledBy("echo").Cached(TimeSpan.FromSeconds(5)));
        Assert.Contains("caching", post.Errors.Keys);

        var outputCache = await host.Manager.ValidateAsync(DynamicEndpoint.Get("/x").HandledBy("echo")
            .WithCaching(new DynamicEndpointCaching { OutputCacheSeconds = 5 }));
        Assert.Contains("AddOutputCache", outputCache.Errors["caching.outputCacheSeconds"].Single());

        var noStore = await host.Manager.ValidateAsync(DynamicEndpoint.Get("/x").HandledBy("echo")
            .WithCaching(new DynamicEndpointCaching { NoStore = true, MaxAgeSeconds = 5, VaryByHeader = ["Bad Header"] }));
        Assert.Contains("caching.noStore", noStore.Errors.Keys);

        var empty = await host.Manager.ValidateAsync(DynamicEndpoint.Get("/x").HandledBy("echo").WithCaching(new DynamicEndpointCaching()));
        Assert.Contains("caching", empty.Errors.Keys);
    }

    [Fact]
    public async Task Rate_limit_of_the_definition_rejects_with_429_per_partition()
    {
        await using var host = await RateLimitedHostAsync();
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/limited").HandledBy("echo")
            .RateLimited(2, TimeSpan.FromMinutes(1), RateLimitPartitionKind.Header, "X-Api-Key"));
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/other").HandledBy("echo")
            .RateLimited(2, TimeSpan.FromMinutes(1), RateLimitPartitionKind.Header, "X-Api-Key"));

        Assert.Equal(HttpStatusCode.OK, (await Get(host, "/limited", "a")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get(host, "/limited", "a")).StatusCode);
        var rejected = await Get(host, "/limited", "a");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(int.Parse(rejected.Headers.GetValues("Retry-After").Single()) > 0);
        var problem = await rejected.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("Too many requests", problem!["title"]!.GetValue<string>());

        // Other clients and other endpoints have their own budgets.
        Assert.Equal(HttpStatusCode.OK, (await Get(host, "/limited", "b")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get(host, "/other", "a")).StatusCode);
    }

    [Fact]
    public async Task Rate_limit_counters_survive_reloads_and_start_over_when_the_limit_changes()
    {
        await using var host = await RateLimitedHostAsync();
        var created = await host.Manager.CreateAsync(DynamicEndpoint.Get("/limited").HandledBy("echo")
            .RateLimited(1, TimeSpan.FromMinutes(1), RateLimitPartitionKind.Endpoint));

        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/limited")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await host.Client.GetAsync("/limited")).StatusCode);

        await host.Manager.ReloadAsync();
        await host.Manager.UpdateAsync(created with { Name = "renamed" });          // the limit itself is unchanged
        Assert.Equal(HttpStatusCode.TooManyRequests, (await host.Client.GetAsync("/limited")).StatusCode);

        var current = (await host.Manager.GetAsync(created.Id))!.Definition;
        await host.Manager.UpdateAsync(current with { RateLimit = current.RateLimit! with { PermitLimit = 2 } });
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/limited")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/limited")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await host.Client.GetAsync("/limited")).StatusCode);
    }

    [Fact]
    public async Task Quota_limits_requests_on_top_of_the_rate_limit()
    {
        await using var host = await RateLimitedHostAsync();
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/quota").HandledBy("echo").WithRateLimit(new DynamicEndpointRateLimit
        {
            Algorithm = RateLimitAlgorithm.TokenBucket,
            PermitLimit = 100,
            WindowSeconds = 1,
            Quota = new DynamicEndpointQuota { Limit = 2, Period = QuotaPeriod.Day },
        }));

        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/quota")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/quota")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await host.Client.GetAsync("/quota")).StatusCode);
    }

    [Fact]
    public async Task Invalid_rate_limits_are_rejected_and_limits_are_documented()
    {
        await using var host = await RateLimitedHostAsync();

        var result = await host.Manager.ValidateAsync(DynamicEndpoint.Get("/x").HandledBy("echo").RequireRateLimiting("named")
            .WithRateLimit(new DynamicEndpointRateLimit { PermitLimit = 0, PartitionBy = RateLimitPartitionKind.Header, SegmentsPerWindow = 2 }));
        Assert.Contains("rateLimit", result.Errors.Keys);
        Assert.Contains("rateLimit.permitLimit", result.Errors.Keys);
        Assert.Contains("rateLimit.partitionHeader", result.Errors.Keys);
        Assert.Contains("rateLimit.segmentsPerWindow", result.Errors.Keys);

        await host.Manager.CreateAsync(DynamicEndpoint.Get("/documented").HandledBy("echo")
            .RateLimited(10, TimeSpan.FromMinutes(1), quota: new DynamicEndpointQuota { Limit = 1000 })
            .Cached(TimeSpan.FromSeconds(30), eTag: true));
        var operation = (await host.Client.GetFromJsonAsync<JsonObject>("/openapi/dynamic.json"))!["paths"]!["/documented"]!["get"]!;

        Assert.NotNull(operation["responses"]!["429"]!["headers"]!["Retry-After"]);
        Assert.NotNull(operation["responses"]!["304"]);
        Assert.Equal("public, max-age=30", operation["responses"]!["200"]!["headers"]!["Cache-Control"]!["example"]!.GetValue<string>());
        Assert.Contains(operation["parameters"]!.AsArray(), p => p!["name"]!.GetValue<string>() == "If-None-Match");
        Assert.Contains("10 requests per 60 s per IP address; quota 1000 requests per day", operation["description"]!.GetValue<string>());
        Assert.Equal(10, operation["x-dynamic-endpoint"]!["rateLimit"]!["permitLimit"]!.GetValue<int>());
    }

    private static Task<TestHost> RateLimitedHostAsync() => TestHost.StartAsync(
        configure: b => b.Services.AddRateLimiter(_ => { }),
        configureApp: app => app.UseRateLimiter());

    private static async Task<HttpResponseMessage> Get(TestHost host, string path, string apiKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Api-Key", apiKey);
        return await host.Client.SendAsync(request);
    }

    private static string? ResponseCachingHeader(DynamicEndpointDefinition definition) =>
        Runtime.ResponseCaching.CacheControl(definition);
}
