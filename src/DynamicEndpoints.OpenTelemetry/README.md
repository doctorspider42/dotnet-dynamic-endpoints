# DynamicEndpoints.OpenTelemetry

Registers the metrics and traces of [DynamicEndpoints](https://www.nuget.org/packages/DynamicEndpoints) with OpenTelemetry.
The core library emits them through `System.Diagnostics` (`Meter`, `ActivitySource`) and has no OpenTelemetry dependency.
This package only adds the one-liners.

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/doctorspider42/dotnet-dynamic-endpoints/blob/main/LICENSE)

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddDynamicEndpointsInstrumentation())
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddDynamicEndpointsInstrumentation())
    .UseOtlpExporter();
```

Without this package: `m.AddMeter(DynamicEndpointsTelemetry.MeterName)` and `t.AddSource(DynamicEndpointsTelemetry.ActivitySourceName)`.

## Metrics (meter `DynamicEndpoints`)

| Instrument | Type | Extra tags |
|---|---|---|
| `dynamic_endpoints.requests` | counter | `dynamic_endpoint.outcome` (`processed`, `rejected`, `short_circuited`, `error`), `http.response.status_code` |
| `dynamic_endpoints.request.duration` | histogram (s) | same as above |
| `dynamic_endpoints.validation.failures` | counter | `dynamic_endpoint.validation.layer` (`binding`, `constraints`, `parameter_validators`, `rules`, `request_validators`) |
| `dynamic_endpoints.processor.duration` | histogram (s) | |
| `dynamic_endpoints.errors` | counter | `error.type` |

Every measurement carries `dynamic_endpoint.id`, `dynamic_endpoint.name`, `dynamic_endpoint.processor`, `http.route` and
`http.request.method`.

## Traces (source `DynamicEndpoints`)

`DynamicEndpoints.Request` with the children `DynamicEndpoints.Filters`, `DynamicEndpoints.Binding`,
`DynamicEndpoints.Validation.{layer}` (one per layer that ran) and `DynamicEndpoints.Processor`. Failed validation layers and
exceptions set the span status to `Error`.

📖 [Documentation](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/telemetry.html) ·
📝 [Changelog](https://github.com/doctorspider42/dotnet-dynamic-endpoints/blob/main/CHANGELOG.md)
