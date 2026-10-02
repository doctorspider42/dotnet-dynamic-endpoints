# DynamicEndpoints

**Runtime-defined HTTP endpoints for ASP.NET Core.** Click it in a panel → it's live. Restart the app → it's still there.

[![CI](https://github.com/doctorspider42/dotnet-dynamic-endpoints/actions/workflows/ci.yml/badge.svg)](https://github.com/doctorspider42/dotnet-dynamic-endpoints/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/doctorspider42/dotnet-dynamic-endpoints/blob/main/LICENSE)
[![Dependencies](https://img.shields.io/badge/third--party%20dependencies-0-brightgreen)](https://github.com/doctorspider42/dotnet-dynamic-endpoints)

Admins **configure** endpoints (route, parameters, validation, business rules). Developers write the **building blocks**
(processors and validators). Endpoints are real ASP.NET Core `RouteEndpoint`s: they are created and removed at runtime,
validated on save, persisted, and documented as OpenAPI 3.1.

## Features

- 🔥 **Hot endpoints:** add, change, disable and delete at runtime. The routing table swaps atomically.
- 🧩 **Declarative binding:** route, query, header, JSON body and form parameters with types, defaults and request names.
- 📎 **File uploads:** `multipart/form-data` with size and content-type limits, documented as binary in OpenAPI.
- 🛡️ **Validation:** JSON Schema constraints, built-in formats (e-mail, URI, phone, IP, time), JsonLogic business rules and custom validators.
- 🪝 **Filters:** access checks before validation, your own error format, logging and metering of rejected requests.
- 🌍 **Localized errors:** English and Polish built in, overridable texts, stable error codes.
- 📝 **Drafts & history:** publish changes now or at a set time, diff revisions, roll back.
- 📈 **Metrics & tracing:** per-endpoint requests, validation failures by layer, processor duration and spans via `System.Diagnostics`.
- ⚙️ **Processors:** validated input goes to your own, statically typed code with full DI.
- 🔋 **Built-in processors (opt-in):** HTTP forward, webhook with retries and signatures, response templates.
- ⏱️ **Per-endpoint caching and rate limits:** `Cache-Control`, ETags, output caching, rate limits and quotas in the definition.
- 🔁 **GitOps:** stable export, import with upsert/sync and dry-run diffs, import from OpenAPI, request snippets.
- 📜 **OpenAPI 3.1:** generated from the definitions, ready for Swagger UI, with security schemes, common headers and hooks.
- 🔐 **First-class endpoints:** authorization policies, rate limiting and conventions work as usual.
- 🚦 **Safe by design:** no code execution, ReDoS-proof regexes, size limits, conflict detection.
- 🪶 **Zero third-party dependencies.**

## Quick start

```csharp
builder.Services
    .AddDynamicEndpoints()
    .AddFromAssemblyContaining<Program>();          // processors, validators, seeders

var app = builder.Build();

app.MapDynamicEndpoints();                                        // the dynamic routes
app.MapDynamicEndpointsAdmin("/api/admin/endpoints")              // management REST API
   .RequireAuthorization("admin");
app.MapDynamicEndpointsOpenApi("/openapi/dynamic.json");          // OpenAPI 3.1
```

Define an endpoint from code (or let an admin do it through the API):

```csharp
await manager.CreateAsync(DynamicEndpoint.Post("/orders")
    .HandledBy<OrderProcessor, OrderConfig>(new() { Queue = "incoming" })
    .FromHeader("tenantId", p => p.BindFrom("X-Tenant-Id").Required())
    .FromBody("quantity", p => p.Integer().Required().Range(1, 100))
    .FromBody("email", p => p.Email().Required())
    .WithRule("""{ ">=": [{ "var": "quantity" }, 10] }""", "Wholesale orders only.", "quantity"));
```

```csharp
[DynamicProcessor("orders")]
public sealed class OrderProcessor(IBus bus) : DynamicEndpointProcessor<OrderConfig>
{
    protected override async Task<IResult> ProcessAsync(DynamicRequest request, OrderConfig config)
    {
        await bus.Send(config.Queue, request.Parameters, request.RequestAborted); // already bound & validated
        return Results.Accepted();
    }
}
```

The default store is in-memory. For persistence add
[DynamicEndpoints.EntityFrameworkCore](https://www.nuget.org/packages/DynamicEndpoints.EntityFrameworkCore).
For FluentValidation support add [DynamicEndpoints.FluentValidation](https://www.nuget.org/packages/DynamicEndpoints.FluentValidation).
For instant propagation across instances add [DynamicEndpoints.PostgreSql](https://www.nuget.org/packages/DynamicEndpoints.PostgreSql)
(`LISTEN/NOTIFY`) or [DynamicEndpoints.Redis](https://www.nuget.org/packages/DynamicEndpoints.Redis) (pub/sub), for tests
[DynamicEndpoints.Testing](https://www.nuget.org/packages/DynamicEndpoints.Testing), and for OpenTelemetry
[DynamicEndpoints.OpenTelemetry](https://www.nuget.org/packages/DynamicEndpoints.OpenTelemetry).

## Learn more

- 📖 [Documentation, screenshots & sample app](https://github.com/doctorspider42/dotnet-dynamic-endpoints#readme)
- 📝 [Changelog](https://github.com/doctorspider42/dotnet-dynamic-endpoints/blob/main/CHANGELOG.md)
- 🐛 [Issues](https://github.com/doctorspider42/dotnet-dynamic-endpoints/issues)
