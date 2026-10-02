# 10 – Caching & rate limits

Response caching (`Cache-Control`, ETags with `304 Not Modified`, ASP.NET Core output caching) and rate limits with quotas
live in the endpoint definition – admins set them per endpoint, without a deployment, and ASP.NET Core enforces them. A rejected
request is a `429` with `Retry-After`, built by the error factory, so it has the application's format. Every endpoint is
answered by a `stamp` processor with a call counter and the time it ran: a cached response shows the old values.

| Endpoint | Setting | Try |
|---|---|---|
| `GET /prices/{sku}` | `Cached(60 s, eTag: true, outputCache: 30 s)` | the same `call` twice in a row; `If-None-Match` → `304` |
| `GET /catalog` | output cache with `VaryByHeader = ["Accept-Language"]` | one cache entry per language, `Vary: Accept-Language` |
| `GET /balance` | `NoStore` | `Cache-Control: no-store`, a new `call` every time |
| `GET /quotes` | `RateLimited(3, 1 min)` per client IP | the 4th request in a minute is a `429` |
| `GET /search?q=` | 5 per minute per `X-Api-Key`, quota 20 per day | each key has its own budget |
| `POST /exports` | token bucket of 2, one token per 10 s, one budget for everybody | two quick calls pass, the third is a `429` until a token is back |

| File | What to look at |
|---|---|
| [`Program.cs`](Program.cs) | `AddRateLimiter()` / `UseRateLimiter()`, `AddOutputCache()` / `UseOutputCache()`, `UseErrorResponses(…)` with `DynamicErrorKind.TooManyRequests` |
| [`CachingSeeder.cs`](CachingSeeder.cs) | `Cached(…)`, `WithCaching(…)`, `RateLimited(…)` with partitions and a quota, `WithRateLimit(…)` with a token bucket |
| [`caching-and-rate-limits.http`](caching-and-rate-limits.http) | the requests below |

## Run it

```bash
dotnet run --project samples/10-CachingAndRateLimits
```

Definitions in `caching-and-rate-limits.db` (in the sample's folder, the working directory of `dotnet run`). Panel:
<http://localhost:5110/admin/>, Swagger UI: <http://localhost:5110/swagger> – the OpenAPI document shows the caching headers,
`304`, the limit and the `429`.

## Click around

- Open *Price (cached)*: the *Caching & rate limits* section of the editor has max age, visibility, no-store, ETag, output cache
  and vary settings, and a rate limit with algorithm, partition and an optional quota.
- Give *Quotes* a limit of 1 and save: the counters start fresh, because they belong to the limit settings. Edits elsewhere keep them.
- Call *Quotes* four times in the *Try* console.

## Call it

```bash
curl -i http://localhost:5110/prices/ANV-1           # Cache-Control: public, max-age=60, ETag: "…"
curl http://localhost:5110/prices/ANV-1              # the same "call" and "generatedAt" – output cache
curl -i -H 'If-None-Match: "<etag>"' http://localhost:5110/prices/ANV-1    # 304 Not Modified, no body

curl -H "Accept-Language: pl" http://localhost:5110/catalog
curl -H "Accept-Language: en" http://localhost:5110/catalog                # another cache entry
curl -i http://localhost:5110/balance                # Cache-Control: no-store

for i in 1 2 3 4; do curl -s -o /dev/null -w "%{http_code}\n" http://localhost:5110/quotes; done   # 200 200 200 429
curl -i http://localhost:5110/quotes                 # 429, Retry-After, {"error":"rate_limited", …}

curl -H "X-Api-Key: key-a" "http://localhost:5110/search?q=anvil"   # 5 per minute for key-a…
curl -H "X-Api-Key: key-b" "http://localhost:5110/search?q=anvil"   # …and another 5 for key-b
```

With several instances the counters are per instance (a client can get up to N times the limit), and with a reverse proxy in
front call `UseForwardedHeaders()` before `UseRateLimiter()`, so the `IpAddress` partition sees the client.

## Read more

- [Caching & rate limits](../../docs/articles/caching-and-rate-limits.md) – every setting, partitions, quotas, multi-tenancy
- [One error format](../../docs/articles/error-responses.md) · [OpenAPI](../../docs/articles/openapi.md) · [Multiple instances](../../docs/articles/multiple-instances.md)
- Next: [11 – Observability](../11-Observability/README.md), or the [overview of all samples](../../docs/articles/samples.md).
