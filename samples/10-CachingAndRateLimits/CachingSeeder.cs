namespace DynamicEndpoints.Samples.CachingAndRateLimits;

/// <summary>
/// Caching and limits live in the definition, so admins set them per endpoint – in the panel's *Caching & rate limits* section –
/// without a deployment. Every endpoint here uses the "stamp" processor (Program.cs), which answers with the time it ran, so you
/// can see when a response came from a cache.
/// </summary>
public sealed class CachingSeeder(IDynamicEndpointManager manager) : IDynamicEndpointSeeder
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if ((await manager.ListAsync(cancellationToken)).Count > 0)
        {
            return;
        }

        DynamicEndpointDefinition[] definitions =
        [
            // Cache-Control: public, max-age=60, an ETag (If-None-Match → 304 without a body) and 30 s in the server's output cache.
            DynamicEndpoint.Get("/prices/{sku}")
                .Named("Price (cached)")
                .InGroup("Caching")
                .HandledBy("stamp")
                .FromRoute("sku", p => p.String().Pattern("^[A-Z]{3}-[0-9]{1,4}$").Example("ANV-1"))
                .Cached(TimeSpan.FromSeconds(60), eTag: true, outputCache: TimeSpan.FromSeconds(30)),

            // The output cache keeps one entry per language: Accept-Language is part of the key (and sent as Vary).
            DynamicEndpoint.Get("/catalog")
                .Named("Catalog (vary by language)")
                .InGroup("Caching")
                .HandledBy("stamp")
                .WithCaching(new DynamicEndpointCaching { MaxAgeSeconds = 30, OutputCacheSeconds = 30, VaryByHeader = ["Accept-Language"] }),

            // Never stored, by anybody.
            DynamicEndpoint.Get("/balance")
                .Named("Balance (no-store)")
                .InGroup("Caching")
                .HandledBy("stamp")
                .WithCaching(new DynamicEndpointCaching { NoStore = true }),

            // 3 requests per minute per client IP – the 4th is a 429 with Retry-After, built by the error factory.
            DynamicEndpoint.Get("/quotes")
                .Named("Quotes (3 per minute)")
                .InGroup("Rate limits")
                .HandledBy("stamp")
                .RateLimited(3, TimeSpan.FromMinutes(1)),

            // Per API key (requests without one by IP): 5 per minute, and a quota of 20 per day on top.
            DynamicEndpoint.Get("/search")
                .Named("Search (per API key, with a quota)")
                .InGroup("Rate limits")
                .HandledBy("stamp")
                .FromQuery("q", p => p.String().MaxLength(50))
                .RateLimited(5, TimeSpan.FromMinutes(1), RateLimitPartitionKind.Header, "X-Api-Key",
                    quota: new DynamicEndpointQuota { Limit = 20, Period = QuotaPeriod.Day }),

            // A token bucket shared by everybody: bursts of 2, one more token every 10 seconds.
            DynamicEndpoint.Post("/exports")
                .Named("Start export (token bucket)")
                .InGroup("Rate limits")
                .HandledBy("stamp")
                .WithRateLimit(new DynamicEndpointRateLimit
                {
                    Algorithm = RateLimitAlgorithm.TokenBucket,
                    PermitLimit = 2,
                    TokensPerPeriod = 1,
                    WindowSeconds = 10,
                    PartitionBy = RateLimitPartitionKind.Endpoint,
                }),
        ];

        foreach (var definition in definitions)
        {
            await manager.CreateAsync(definition, cancellationToken);
        }
    }
}
