---
_layout: landing
---

# DynamicEndpoints

**Runtime-defined HTTP endpoints for ASP.NET Core.** Click it in a panel → it's live. Restart the app → it's still there.

Admins *configure* endpoints and developers write the *building blocks*. Endpoints are real ASP.NET Core `RouteEndpoint`s,
created and removed at runtime, persisted in your database and documented in OpenAPI.

```
admin clicks "publish"  →  validated  →  persisted  →  routable on every instance. No restart, no recompile.
```

<img src="images/admin-list.jpg" alt="Admin panel with runtime-defined endpoints" width="820">

## Where to start

- [Getting started](articles/getting-started.md): packages, the first endpoint and the code behind it.
- [How it works](articles/how-it-works.md): routing, compilation, persistence.
- [Validation](articles/validation.md): JSON Schema constraints, custom validators, JsonLogic rules, FluentValidation.
- [Admin panel](articles/admin-ui.md): `MapDynamicEndpointsAdminUI()`, with drafts, history and a *Try* console.
- [Drafts, history & rollback](articles/drafts-and-history.md): publish now or later, diff revisions, roll back.
- [Export, import & GitOps](articles/export-import-gitops.md): definitions in Git, the `dynamic-endpoints` CLI.
- [Project template](articles/project-template.md): `dotnet new dynamic-endpoints`.
- [API reference](api/DynamicEndpoints.yml): generated from the XML documentation of the packages.

## Packages

| Package | What |
|---|---|
| `DynamicEndpoints` | core: routing, binding, validation engines, manager, drafts & history, admin API, OpenAPI, built-in processors, analyzers. **Zero third-party dependencies** |
| `DynamicEndpoints.AdminUI` | the [admin panel](articles/admin-ui.md), `MapDynamicEndpointsAdminUI()` |
| `DynamicEndpoints.EntityFrameworkCore` | persistence with EF Core, history and drafts included |
| `DynamicEndpoints.FluentValidation` | FluentValidation validators as dynamic validators |
| `DynamicEndpoints.PostgreSql` | instant multi-instance propagation through `LISTEN/NOTIFY` |
| `DynamicEndpoints.Redis` | instant multi-instance propagation through Redis pub/sub |
| `DynamicEndpoints.OpenTelemetry` | `AddDynamicEndpointsInstrumentation()` for OpenTelemetry [metrics and tracing](articles/telemetry.md) |
| `DynamicEndpoints.Sql` | read-only, parameterized [SQL query processor](articles/built-in-processors.md#sql-dynamicendpointssql) for any ADO.NET provider |
| `DynamicEndpoints.Yaml` | YAML for [export, import](articles/export-import-gitops.md) and the [OpenAPI import](articles/openapi-import.md) |
| `DynamicEndpoints.Testing` | in-memory store for `WebApplicationFactory`, test server, in-memory notifier |
| `DynamicEndpoints.Cli` | `dynamic-endpoints` .NET tool: list, export, diff, push, import-openapi – [GitOps](articles/export-import-gitops.md) from CI |
| `DynamicEndpoints.Templates` | `dotnet new dynamic-endpoints` project template |
