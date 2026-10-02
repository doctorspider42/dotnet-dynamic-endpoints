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
- [Project template](articles/project-template.md): `dotnet new dynamic-endpoints`.
- [API reference](api/DynamicEndpoints.yml): generated from the XML documentation of every package.

## Packages

| Package | What |
|---|---|
| `DynamicEndpoints` | core: routing, binding, validation engines, manager, admin API, OpenAPI, analyzers. **Zero third-party dependencies** |
| `DynamicEndpoints.EntityFrameworkCore` | persistence with EF Core |
| `DynamicEndpoints.FluentValidation` | FluentValidation validators as dynamic validators |
| `DynamicEndpoints.PostgreSql` | instant multi-instance propagation through `LISTEN/NOTIFY` |
| `DynamicEndpoints.Redis` | instant multi-instance propagation through Redis pub/sub |
| `DynamicEndpoints.Testing` | in-memory store for `WebApplicationFactory`, test server, in-memory notifier |
| `DynamicEndpoints.Templates` | `dotnet new dynamic-endpoints` project template |
