# Metrics & tracing: OpenTelemetry

The core emits metrics through a `Meter` and spans through an `ActivitySource`, both named `DynamicEndpoints`. There's no
OpenTelemetry dependency: wire them up with the `DynamicEndpoints.OpenTelemetry` package…

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddDynamicEndpointsInstrumentation())
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddDynamicEndpointsInstrumentation())
    .UseOtlpExporter();
```

…or with the constants: `m.AddMeter(DynamicEndpointsTelemetry.MeterName)`, `t.AddSource(DynamicEndpointsTelemetry.ActivitySourceName)`.
`dotnet-counters monitor --counters DynamicEndpoints` works without any setup.

## Metrics

| Metric | Type | Extra tags |
|---|---|---|
| `dynamic_endpoints.requests` | counter | `dynamic_endpoint.outcome` (`processed`, `rejected`, `short_circuited`, `error`), `http.response.status_code` |
| `dynamic_endpoints.request.duration` | histogram (s) | same as above |
| `dynamic_endpoints.validation.failures` | counter | `dynamic_endpoint.validation.layer`: `binding`, `constraints`, `parameter_validators`, `rules`, `request_validators` |
| `dynamic_endpoints.processor.duration` | histogram (s) | |
| `dynamic_endpoints.errors` | counter | `error.type` |

- **Per endpoint:** every measurement carries `dynamic_endpoint.id`, `dynamic_endpoint.name`, `dynamic_endpoint.processor`,
  `http.route` and `http.request.method`. ASP.NET Core's own `http.server.request.duration` gets the dynamic route template too.
- **Cheap when unused:** instruments are only fed when something listens, and spans only exist for sampled requests.

## Traces

`DynamicEndpoints.Request` with the children `DynamicEndpoints.Filters`, `DynamicEndpoints.Binding`,
`DynamicEndpoints.Validation.{layer}` (one per layer that ran) and `DynamicEndpoints.Processor`, below the ASP.NET Core request
span. Every span carries the endpoint tags above plus `dynamic_endpoint.revision`; validation spans add
`dynamic_endpoint.validation.layer` and the number of errors (`dynamic_endpoint.validation.errors`). Failed layers and exceptions
set the span status to `Error`.

All names are constants in `DynamicEndpointsTelemetry` (`Instruments`, `Activities`, `Tags`, `Outcomes`, `ValidationLayers`).

## Together with the rest

- **Multiple instances & Aspire:** the [Aspire AppHost](aspire-and-demo.md) runs the showcase app twice and shows the
  `dynamic_endpoints.*` instruments per endpoint and the spans in the Aspire dashboard.
- **Multi-tenancy:** there is no tenant tag. Tenant endpoints are separate endpoints, so `dynamic_endpoint.id` tells them apart
  even on the same route; `http.route` is the route of the definition, without a tenant route prefix.
- **Revisions:** `dynamic_endpoint.revision` on the spans ties a trace to the [revision](drafts-and-history.md) that served it.
