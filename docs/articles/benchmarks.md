# Benchmarks

`tests/DynamicEndpoints.Benchmarks` (BenchmarkDotNet, in-process TestServer, in-memory store, logging off) compares dynamic
endpoints with equivalent hand-written minimal APIs in the same application, and measures the cost of a routing table swap.
Every benchmark is a full round trip: `HttpClient` → TestServer → response body read and status code checked.

## Requests

| Scenario | Minimal API | Dynamic endpoint | Allocated (minimal → dynamic) |
|---|---:|---:|---:|
| GET `/items/{id}` (int route parameter) | ~12–100 µs | ~33–100 µs | 8.7 KB → 11.6 KB |
| POST JSON, 3 validated fields (valid) | ~53–110 µs | ~42–80 µs | 10.9 KB → 30.5 KB |
| POST JSON, 3 validated fields (invalid → 400) | ~73 µs | ~75–165 µs | 12.3 KB → 34.1 KB |
| POST form (urlencoded, 3 fields) | ~49–58 µs | ~42–88 µs | 12.2 KB → 15.3 KB |

- The minimal APIs are the equivalent hand-written endpoints: `MapGet("/static/items/{id:int}", …)`, a record with hand-written
  checks returning `ValidationProblem` (required, max length 50, range 1..100, e-mail), and a `[FromForm]` record.
- Per-request timings were within run-to-run noise on the test machine (the ranges are from three runs), so no reliable time
  overhead could be measured. Allocations were identical in every run: a dynamic endpoint allocates about 1.3× for GET and forms,
  and about 2.8× for JSON bodies (parsing into a `JsonObject`, schema and rule evaluation, error responses).

## Routing table swap

Changing one endpoint rebuilds the whole routing table, and the change token makes ASP.NET Core rebuild its matcher:

| Endpoints | `UpsertAsync` (one endpoint) | `ReloadAsync` (one changed in store) | Request, no change |
|---:|---:|---:|---:|
| 10 | ~78 µs / 87 KB | ~94 µs / 103 KB | ~31 µs |
| 100 | ~0.38 ms / 584 KB | ~2.6 ms / 858 KB | ~28 µs |
| 1000 | ~18 ms / 5.5 MB | ~36 ms / 8.4 MB | ~85 µs |

The cost grows with the number of endpoints; `ReloadAsync` (a change picked up from another instance) also reads and copies every
definition from the store. The steady-state request cost barely depends on the number of endpoints.

## Running them

Always in Release:

```bash
dotnet run -c Release --project tests/DynamicEndpoints.Benchmarks -- --filter * --job short
dotnet run -c Release --project tests/DynamicEndpoints.Benchmarks -- --filter *RoutingSwap* --job short
```

> Results are from a short job (`--warmupCount 5 --iterationCount 15`) on a dev laptop (AMD Ryzen 7 PRO 5850U, Windows 11,
> .NET 10.0.8, BenchmarkDotNet 0.15.8) and are indicative only. Run the benchmarks on your own hardware before drawing conclusions.
