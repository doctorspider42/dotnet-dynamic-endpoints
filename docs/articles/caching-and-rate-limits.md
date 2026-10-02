# Caching, rate limits & quotas

Both live in the definition, so admins set them per endpoint without a deployment. They are built on ASP.NET Core output caching
and rate limiting.

## Response caching: `Cache-Control`, ETags, output caching

```csharp
DynamicEndpoint.Get("/prices/{sku}")
    .Cached(TimeSpan.FromMinutes(1), eTag: true, outputCache: TimeSpan.FromSeconds(30))
    // or .WithCaching(new DynamicEndpointCaching { MaxAgeSeconds = 60, ETag = true, OutputCacheSeconds = 30, VaryByHeader = ["Accept-Language"] })
```

```json
"caching": { "maxAgeSeconds": 60, "eTag": true, "outputCacheSeconds": 30 }
```

- **`Cache-Control`:** `maxAgeSeconds` and `visibility` (`Public`, or `Private` by default for endpoints that require authorization;
  those can't be public). `noStore: true` forbids caching, also for non-GET endpoints.
- **ETags:** computed from the response body. A matching `If-None-Match` gets `304 Not Modified` without a body. Without a max age
  clients are told to revalidate every time (`no-cache`).
- **Output caching** (`outputCacheSeconds`, `outputCachePolicy`): ASP.NET Core output caching, so the application needs
  `services.AddOutputCache()` and `app.UseOutputCache()` (checked on save). The cache key contains the path, the query string
  (or `varyByQuery`) and the endpoint's header parameters (plus `varyByHeader`, also sent as `Vary`). Entries are evicted on every
  instance when the definition changes. Requests with `Authorization` or cookies are not cached (the framework's default policy).
- Headers are only added to successful responses. Caching is limited to GET endpoints and documented in OpenAPI
  (`Cache-Control`/`ETag` headers, `If-None-Match`, `304`).

## Rate limits and quotas

No named policy needed: the limit lives in the endpoint and is enforced by ASP.NET Core rate limiting.

```csharp
builder.Services.AddRateLimiter(_ => { });   // once
app.UseRateLimiter();                        // after UseRouting, when you call it yourself

DynamicEndpoint.Get("/quotes")
    .RateLimited(10, TimeSpan.FromMinutes(1), RateLimitPartitionKind.Header, "X-Api-Key",
        quota: new DynamicEndpointQuota { Limit = 10_000, Period = QuotaPeriod.Day });
    // or .WithRateLimit(new DynamicEndpointRateLimit { Algorithm = RateLimitAlgorithm.TokenBucket, PermitLimit = 20, TokensPerPeriod = 5 })
```

```json
"rateLimit": { "algorithm": "SlidingWindow", "permitLimit": 100, "windowSeconds": 60, "segmentsPerWindow": 6,
               "partitionBy": "User", "quota": { "limit": 5000, "period": "Day" } }
```

- **Algorithms:** `FixedWindow` (default), `SlidingWindow`, `TokenBucket` (`permitLimit` is the bucket size, `tokensPerPeriod` the
  refill), `Concurrency`. `windowSeconds` defaults to 60. `queueLimit` lets requests wait instead of failing.
- **Partitions:** `IpAddress` (default; use `UseForwardedHeaders` behind a proxy), `User` (anonymous requests by IP), `Header`
  (e.g. an API key; requests without it by IP) or `Endpoint` (one budget for everybody).
- **Quota:** a second, long-term limit (`Hour`, `Day`, `Week`, `Month` = 30 days) for the same partitions. Windows start with the
  first request of a partition, not on calendar boundaries.
- **Rejections:** `429 Too Many Requests` with `Retry-After`, built by `IDynamicErrorResponseFactory` (`DynamicErrorKind.TooManyRequests`,
  see [One error format](error-responses.md)).
- **Changes:** counters belong to the limit settings. Changing them starts fresh counters, reloads and unrelated edits keep them.
  Counters are kept in memory per instance, so with N instances a client can get up to N times the limit.
- A definition uses either `rateLimitingPolicy` (a named policy) or `rateLimit`, not both. OpenAPI documents the limit and the `429`.

## With multi-tenancy

- **Budgets are per endpoint.** Every tenant endpoint has its own counters, so tenants on the same route don't share a limit.
  A shared endpoint has one budget for all tenants – partition by `Header` or `User` to give each client its own.
- **Output caching and the tenant header.** The output cache key is built from the request (path, query, vary headers), not from
  the endpoint that answered. With `FromHost()` or `FromRoutePrefix()` the tenant is part of the key already; with
  `FromHeader("X-Tenant-Id")`, add the header to `varyByHeader`, or tenants with the same route can be served each other's cached
  responses. The same goes for shared endpoints whose response depends on `request.Tenant`.
