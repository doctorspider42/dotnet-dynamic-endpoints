# 11 – Observability: metrics & traces

Every dynamic endpoint is measured and traced on its own: requests by outcome, validation failures by layer, processor duration,
errors – each measurement tagged with the endpoint's id, name, processor, route and method – and a span per step (filters,
binding, every validation layer, the processor). The core uses `System.Diagnostics` only; `DynamicEndpoints.OpenTelemetry`
hooks it into OpenTelemetry. Here the signals go to the **console exporter** – no collector, no Docker.

| Endpoint | Shows up as |
|---|---|
| `GET /hello/{name}` | `dynamic_endpoints.requests` with `dynamic_endpoint.outcome = processed` |
| `POST /orders` | `outcome = rejected` and `dynamic_endpoints.validation.failures` with `layer = constraints` (bad SKU) or `rules` (`quantity` above 10); a `DynamicEndpoints.Validation.{layer}` span |
| `GET /work?ms=` | `dynamic_endpoints.processor.duration`, a `DynamicEndpoints.Processor` span as long as the sleep |
| `GET /boom` | `outcome = error`, `dynamic_endpoints.errors` with `error.type`, spans with status `Error`, a `500` problem for the client |

| File | What to look at |
|---|---|
| [`Program.cs`](Program.cs) | `AddOpenTelemetry().WithMetrics(…AddDynamicEndpointsInstrumentation()…).WithTracing(…)`, the console exporter, OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is set |
| [`ObservabilitySeeder.cs`](ObservabilitySeeder.cs) | the four endpoints |
| [`appsettings.json`](appsettings.json) | `Telemetry:Console` and `Telemetry:MetricsIntervalSeconds` (15 s) |
| [`observability.http`](observability.http) | the requests below |

## Run it

```bash
dotnet run --project samples/11-Observability
```

Definitions in `observability.db` (in the sample's folder, the working directory of `dotnet run`). Panel:
<http://localhost:5111/admin/>, Swagger UI: <http://localhost:5111/swagger>. Call a few endpoints and watch the console:
every request prints its activities (`DynamicEndpoints.Request`, `…Binding`, `…Validation.constraints`, `…Processor`, with the
`dynamic_endpoint.*` tags), and every 15 seconds the metrics are printed.

```text
Activity.DisplayName:        DynamicEndpoints.Validation.rules
Activity.Tags:
    dynamic_endpoint.id: 01a0fe78-4dab-75ce-a2eb-b1fe128696ce
    dynamic_endpoint.name: Create order
    http.route: /orders
    http.request.method: POST
    dynamic_endpoint.processor: response
    dynamic_endpoint.revision: 1
    dynamic_endpoint.validation.layer: rules
    dynamic_endpoint.validation.errors: 1
StatusCode: Error
Activity.StatusDescription:  Validation failed.
…
Metric Name: dynamic_endpoints.validation.failures, Description: Requests rejected by a validation layer of a dynamic endpoint., Unit: {request}, Metric Type: LongSum
(…] dynamic_endpoint.id: 01a0fe78-… dynamic_endpoint.name: Create order dynamic_endpoint.processor: response dynamic_endpoint.validation.layer: rules http.request.method: POST http.route: /orders
Value: 1
```

## Without OpenTelemetry, and with a dashboard

- `dotnet-counters monitor -n DynamicEndpoints.Samples.Observability --counters DynamicEndpoints` shows the instruments live,
  without any setup (`dotnet tool install --global dotnet-counters`).
- Any OTLP backend: `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317 dotnet run --project samples/11-Observability`. Turn
  the console off with `--Telemetry:Console=false`.
- **The Aspire dashboard** with all of it – metrics per endpoint, traces, logs – comes with the showcase:
  `dotnet run --project samples/DynamicEndpoints.AppHost` (needs Docker or Podman for its PostgreSQL and Redis). See
  [Sample app, Aspire & live demo](../../docs/articles/aspire-and-demo.md).

## Call it

```bash
curl http://localhost:5111/hello/Ada
curl -X POST http://localhost:5111/orders -H "Content-Type: application/json" -d '{"sku":"nope","quantity":2}'    # constraints
curl -X POST http://localhost:5111/orders -H "Content-Type: application/json" -d '{"sku":"ANV-1","quantity":50}'  # rules
curl "http://localhost:5111/work?ms=1200"
curl -i http://localhost:5111/boom                          # 500 – the exception is logged, not sent
```

## Read more

- [Metrics & tracing](../../docs/articles/telemetry.md) – every instrument, tag and span; `DynamicEndpointsTelemetry` constants
- [Sample app, Aspire & live demo](../../docs/articles/aspire-and-demo.md) · [One error format](../../docs/articles/error-responses.md)
- Back to the [overview of all samples](../../docs/articles/samples.md), or the everything-together [showcase](../Showcase/README.md).
