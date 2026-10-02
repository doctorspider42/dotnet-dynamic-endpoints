<div align="center">

# ⚡ DynamicEndpoints

### Runtime-defined HTTP endpoints for ASP.NET Core.
**Click it in a panel → it's live. Restart the app → it's still there.**

[![CI](https://github.com/doctorspider42/dotnet-dynamic-endpoints/actions/workflows/ci.yml/badge.svg)](https://github.com/doctorspider42/dotnet-dynamic-endpoints/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/DynamicEndpoints?logo=nuget&color=004880)](https://www.nuget.org/packages/DynamicEndpoints)
[![Dependencies](https://img.shields.io/badge/core%20dependencies-0-brightgreen)](#-packages)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![ASP.NET Core](https://img.shields.io/badge/ASP.NET%20Core-minimal%20APIs-512BD4?logo=dotnet&logoColor=white)](https://learn.microsoft.com/aspnet/core/fundamentals/minimal-apis)
[![OpenAPI 3.1](https://img.shields.io/badge/OpenAPI-3.1-6BA539?logo=openapiinitiative&logoColor=white)](https://spec.openapis.org/oas/v3.1.0)
[![JSON Schema 2020-12](https://img.shields.io/badge/JSON%20Schema-2020--12-blue)](https://json-schema.org/)
[![PRs Welcome](https://img.shields.io/badge/PRs-welcome-brightgreen.svg)](#-contributing)
[![GitHub stars](https://img.shields.io/github/stars/doctorspider42/dotnet-dynamic-endpoints?style=social)](https://github.com/doctorspider42/dotnet-dynamic-endpoints/stargazers)

[Quick start](#-quick-start) •
[Features](#-features) •
[Documentation](https://doctorspider42.github.io/dotnet-dynamic-endpoints/) •
[API reference](https://doctorspider42.github.io/dotnet-dynamic-endpoints/api/DynamicEndpoints.html) •
[Changelog](CHANGELOG.md) •
[Sample app](#-sample-app)

<img src="docs/images/admin-list.jpg" alt="Admin panel with runtime-defined endpoints" width="820">

</div>

---

## 🤔 Why?

Your client wants to **define, publish and change HTTP endpoints from an admin panel**, with their own parameters,
their own validation and no deployment. The usual answers are bad:

- ❌ **"We'll add it in the next sprint"**: every new endpoint is a release.
- ❌ **"Admins can write C# in a textbox"**: that's remote code execution shipped as a feature.
- ❌ **A catch-all route with a hand-rolled dispatcher**: you lose authorization, rate limiting, OpenAPI and everything else routing gives you.

**DynamicEndpoints** takes a fourth path. Admins *configure* endpoints and developers write the *building blocks*.
Endpoints are real ASP.NET Core `RouteEndpoint`s, created and removed at runtime, persisted in your database and documented in Swagger.

```
admin clicks "publish"  →  validated  →  persisted  →  routable on every instance. No restart, no recompile.
```

## ✨ Features

| | |
|---|---|
| 🔥 **Hot endpoints** | Add, change, disable and delete endpoints at runtime. Routing swaps atomically, so a request never sees a half-applied state. |
| 🗂️ **Drafts & history** | Save a change as a draft, publish it now or at a set time. Every revision is kept: diff any two, roll back with one call. |
| 💾 **Persistent** | Stored with EF Core (any provider) and loaded on start-up. Optimistic concurrency, ready-made migrations, and saving in *your* transaction. |
| 🧩 **Declarative binding** | Route, query, header, JSON body and form parameters with types, defaults and request names (`X-Tenant-Id` → `tenantId`). |
| 📎 **File uploads** | `multipart/form-data` with size and content-type limits, streamed by ASP.NET Core. No base64, documented as binary in OpenAPI. |
| 🛡️ **Four layers of validation** | JSON Schema constraints, custom C# validators, JsonLogic business rules, FluentValidation. |
| 🪝 **Filters** | Access checks before validation, logging and metering of rejected requests. |
| 🧯 **One error format** | `IDynamicErrorResponseFactory` builds validation errors, 401/403/404/405 and exceptions in your own format. |
| 🌍 **Localized errors** | English and Polish built in, every message overridable, stable error codes for clients. |
| 📧 **Built-in formats** | E-mail, URI, phone (E.164), IPv4/IPv6, time, date, date-time, UUID. No code needed. |
| 🪶 **Zero dependencies** | The core depends on ASP.NET Core only. The JSON Schema subset and the JsonLogic engine are built in. |
| ⚙️ **Processors** | Your code, your DI, your database. Validated input goes to a regular, statically typed handler. |
| 🔋 **Batteries included** | Opt-in HTTP forward, webhook (retries, HMAC), response templates and read-only SQL processors. |
| ⏱️ **Caching & rate limits** | `Cache-Control`, ETags/304, output caching, rate limits and quotas – set per endpoint, in the definition. |
| 🔁 **GitOps** | Stable export, import with upsert/sync and dry-run diffs, the `dynamic-endpoints` CLI for CI, import from OpenAPI with a processor per operation, mock mode and re-import/sync. |
| ✂️ **Snippets** | Example requests and curl, HTTPie and C# snippets for every endpoint. |
| 📜 **OpenAPI 3.1 + Swagger UI** | Generated from the definitions and always current. Business rules show up in the docs. |
| 🔐 **First-class citizens** | Authorization policies, rate limiting, CORS and endpoint conventions behave the same as on hand-written endpoints. |
| 🚦 **Safe by design** | No code execution, ReDoS-proof regexes, body and depth limits, reserved prefixes, conflict detection. |
| 🌐 **Multi-instance** | Instant propagation through PostgreSQL `LISTEN/NOTIFY` or Redis pub/sub, with polling as the fallback. |
| 📣 **Change events** | Created / updated / deleted handlers for audit logs and cache invalidation. |
| 📈 **Metrics & tracing** | Requests, validation failures by layer, processor duration and errors per endpoint, plus spans for binding, every validation layer and the processor. Plain `System.Diagnostics`, ready for OpenTelemetry. |
| 🏢 **Multi-tenancy** | Endpoints per tenant on the same routes, resolved from a header, host, claim or route prefix. Scoped manager, admin API, drafts, history, export/import and OpenAPI. |
| 📝 **Audit log** | Who changed what and when, with a property-by-property diff. `ILogger`, in-memory or EF Core, queryable through the admin API. |
| 🔍 **Analyzers** | Invalid routes, regexes, JsonLogic rules and processor types are reported at build time. |
| 🧰 **Project template** | `dotnet new dynamic-endpoints`: EF Core SQLite, admin API and panel, Swagger UI, a sample processor and seeder. |
| 🧪 **Test kit** | In-memory store for `WebApplicationFactory` and a ready-made test server. No database needed. |
| 🪄 **Assembly scanning** | `AddFromAssemblyContaining<Program>()` registers every processor, validator and seeder in one call. |
| 🖥️ **Admin REST API** | One line, `MapDynamicEndpointsAdmin()`, or build your own on top of `IDynamicEndpointManager`. |
| 🎛️ **Admin panel** | `MapDynamicEndpointsAdminUI()`: editor, drafts, history with diffs and rollback, and a "Try" console that writes curl, HTTPie and C# for you. |
| ✅ **Tested** | Integration tests run on TestServer + SQLite, and on real PostgreSQL and Redis containers: persistence, migrations, multiple instances, concurrency. |

## 🚀 Quick start

```bash
dotnet add package DynamicEndpoints
dotnet add package DynamicEndpoints.EntityFrameworkCore   # persistence
dotnet add package DynamicEndpoints.FluentValidation      # optional
dotnet add package DynamicEndpoints.AdminUI               # optional: the admin panel
```

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite("Data Source=app.db"));

builder.Services
    .AddDynamicEndpoints()
    .AddFromAssemblyContaining<Program>()          // all processors, validators & seeders
    .UseEntityFrameworkStore<AppDbContext>();      // persistence

var app = builder.Build();

app.MapDynamicEndpoints();                                         // 🔥 the dynamic routes
app.MapDynamicEndpointsAdmin("/api/admin/endpoints")               // 🖥️ management API
   .RequireAuthorization("admin");
app.MapDynamicEndpointsAdminUI("/admin", "/api/admin/endpoints")   // 🎛️ admin panel (DynamicEndpoints.AdminUI)
   .RequireAuthorization("admin");
app.MapDynamicEndpointsOpenApi("/openapi/dynamic.json");           // 📜 OpenAPI 3.1
app.UseSwaggerUI(c => c.SwaggerEndpoint("/openapi/dynamic.json", "Dynamic API"));

app.Run();
```

Add the tables to your context with `modelBuilder.ApplyDynamicEndpointsConfiguration();` and you're done. The panel is at `/admin/`.

### Your first endpoint, from code

```csharp
public sealed class OrdersSeeder(IDynamicEndpointManager manager) : IDynamicEndpointSeeder
{
    public async Task SeedAsync(CancellationToken ct)
    {
        if ((await manager.ListAsync(ct)).Count > 0) return;

        await manager.CreateAsync(DynamicEndpoint.Post("/orders/{customerId}")
            .Named("Create order")
            .InGroup("Orders")
            .HandledBy<OrderProcessor, OrderConfig>(new() { Queue = "incoming" })   // typed, no magic strings
            .FromRoute("customerId", p => p.String().Pattern("^C[0-9]{3}$"))
            .FromHeader("tenantId", p => p.BindFrom("X-Tenant-Id").Required())
            .FromBody("quantity", p => p.Integer().Required().Range(1, 100))
            .FromBody("deliveryDate", p => p.Date().Required())
            .FromBody("contactEmail", p => p.Email().Required())
            .FromBody("nip", p => p.ValidatedBy<NipValidator>())
            .WithRule("""{ ">=": [{ "var": "quantity" }, 10] }""", "Wholesale orders only.", "quantity")
            .ValidatedBy<CreditLimitValidator>(), ct);
    }
}
```

…or let an admin click the same thing together in a panel. 🖱️

`HandledBy<T>()` and `ValidatedBy<T>()` resolve the name the same way registration does (the attribute, or the type name),
so code and registration can't drift apart. String names (`HandledBy("orders")`) still work. That's what the panel stores.

### …and the code behind it

```csharp
[DynamicProcessor("orders", Description = "Puts orders on a queue")]
public sealed class OrderProcessor(IBus bus) : DynamicEndpointProcessor<OrderConfig>
{
    protected override async Task<IResult> ProcessAsync(DynamicRequest request, OrderConfig config)
    {
        // request.Parameters is already bound, converted and validated
        await bus.Send(config.Queue, request.Parameters, request.RequestAborted);
        return Results.Accepted();
    }
}
```

## 📚 Documentation

The full documentation, with an API reference generated from the XML docs, is at
**[doctorspider42.github.io/dotnet-dynamic-endpoints](https://doctorspider42.github.io/dotnet-dynamic-endpoints/)**.

| | |
|---|---|
| **Basics** | [Getting started](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/getting-started.html) · [How it works](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/how-it-works.html) · [Project template](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/project-template.html) |
| **Defining endpoints** | [Definition model](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/definition-model.html) · [Validation: four layers, zero recompiles](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/validation.html) · [Processors, validators, seeders & assembly scanning](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/processors-and-validators.html) · [Built-in processors: HTTP forward, webhook, response, SQL](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/built-in-processors.html) · [File uploads & forms](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/file-uploads.html) · [Handing work on](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/handing-work-on.html) |
| **Managing endpoints** | [`IDynamicEndpointManager`](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/managing-endpoints.html) · [Change sets in your transaction](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/change-sets.html) · [Admin REST API](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/admin-api.html) · [Admin panel](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/admin-ui.html) · [Drafts, history & rollback](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/drafts-and-history.html) · [Export, import & GitOps](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/export-import-gitops.html) · [Import from OpenAPI](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/openapi-import.html) · [Change events](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/change-events.html) · [Audit log](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/audit-log.html) · [Multi-tenancy](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/multi-tenancy.html) |
| **Requests & responses** | [Filters](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/filters.html) · [One error format](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/error-responses.html) · [Localized error messages](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/localization.html) · [OpenAPI](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/openapi.html) · [Caching & rate limits](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/caching-and-rate-limits.html) |
| **Hosting** | [Options](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/options.html) · [Multiple instances](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/multiple-instances.html) · [EF Core & migrations](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/ef-core.html) · [Testing](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/testing.html) · [Metrics & tracing](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/telemetry.html) · [Sample app, Aspire & live demo](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/aspire-and-demo.html) · [Analyzers](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/analyzers.html) · [Benchmarks](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/benchmarks.html) |
| **Safety** | [Security](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/security.html) · [Limitations](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/limitations.html) |

The pages live in [`docs/`](docs/) and build with `dotnet tool restore && dotnet docfx docs/docfx.json` (output in `docs/_site`).

## 🔒 Security

No code is executed from definitions, regexes are ReDoS-proof, bodies are size- and depth-limited, and clashes with your own
routes are rejected. **The admin API is open by default**: put `.RequireAuthorization(...)` on it, and on the admin panel. Details in
[Security](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/security.html).

## 🖼️ Screenshots

| Endpoint editor | Swagger UI (OpenAPI 3.1, generated live) |
|---|---|
| <img src="docs/images/editor.jpg" alt="Endpoint editor" width="420"> | <img src="docs/images/swagger.jpg" alt="Swagger UI" width="420"> |

## 📦 Packages

| Package | What |
|---|---|
| `DynamicEndpoints` | core: routing, binding, validation engines, manager, drafts & history, admin API, OpenAPI, built-in processors, analyzers. **Zero third-party dependencies** |
| `DynamicEndpoints.AdminUI` | the admin panel, `MapDynamicEndpointsAdminUI()` |
| `DynamicEndpoints.EntityFrameworkCore` | persistence with EF Core, history and drafts included |
| `DynamicEndpoints.FluentValidation` | FluentValidation validators as dynamic validators |
| `DynamicEndpoints.PostgreSql` | instant multi-instance propagation through `LISTEN/NOTIFY` |
| `DynamicEndpoints.Redis` | instant multi-instance propagation through Redis pub/sub |
| `DynamicEndpoints.OpenTelemetry` | `AddDynamicEndpointsInstrumentation()` for OpenTelemetry metrics and tracing |
| `DynamicEndpoints.Sql` | read-only, parameterized SQL query processor for any ADO.NET provider |
| `DynamicEndpoints.Yaml` | YAML for export, import and the OpenAPI import |
| `DynamicEndpoints.Testing` | in-memory store for `WebApplicationFactory`, test server, in-memory notifier |
| `DynamicEndpoints.Cli` | `dynamic-endpoints` .NET tool: list, export, diff, push, import-openapi – GitOps from CI |
| `DynamicEndpoints.Templates` | `dotnet new dynamic-endpoints` project template |

## ⏱️ Benchmarks

Dynamic endpoints vs. equivalent hand-written minimal APIs, in-process (TestServer), from
[`tests/DynamicEndpoints.Benchmarks`](tests/DynamicEndpoints.Benchmarks):

| Scenario | Minimal API | Dynamic endpoint | Allocated (minimal → dynamic) |
|---|---:|---:|---:|
| GET `/items/{id}` (int route parameter) | ~12–100 µs | ~33–100 µs | 8.7 KB → 11.6 KB |
| POST JSON, 3 validated fields (valid) | ~53–110 µs | ~42–80 µs | 10.9 KB → 30.5 KB |
| POST JSON, 3 validated fields (invalid → 400) | ~73 µs | ~75–165 µs | 12.3 KB → 34.1 KB |
| POST form (urlencoded, 3 fields) | ~49–58 µs | ~42–88 µs | 12.2 KB → 15.3 KB |

| Endpoints | `UpsertAsync` (one endpoint, routing table swap) | `ReloadAsync` (one changed in store) |
|---:|---:|---:|
| 10 | ~78 µs / 87 KB | ~94 µs / 103 KB |
| 100 | ~0.38 ms / 584 KB | ~2.6 ms / 858 KB |
| 1000 | ~18 ms / 5.5 MB | ~36 ms / 8.4 MB |

Short job on a dev laptop (Ryzen 7 PRO 5850U, Windows 11, .NET 10.0.8): request timings were within run-to-run noise (ranges from
three runs), allocations were stable. Run them yourself with
`dotnet run -c Release --project tests/DynamicEndpoints.Benchmarks -- --filter * --job short`. More in [Benchmarks](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/benchmarks.html).

## 🧪 Sample app

```bash
dotnet run --project samples/DynamicEndpoints.Sample
```

- 🖥️ **Admin panel:** `http://localhost:5118/admin/`, from the `DynamicEndpoints.AdminUI` package. Editor with processor forms,
  caching and rate limits, drafts and history, export/import, OpenAPI import, audit log and a "Try" console with snippets.
- 🏢 **Tenants:** tenant picked by the `X-Tenant` header; acme's own panel at `http://localhost:5118/admin/tenants/acme/`.
- 📜 **Swagger UI:** `http://localhost:5118/swagger`, with the *Dynamic endpoints* and *Admin API* documents.
- 🧩 **Processors:** `echo`, `template`, `calculator`, `collection` and an inline `clock`, plus the built-in `http-forward`,
  `webhook`, `response` and `sql-query`.
- 🌱 **Seeded demos:** `GET /products/{sku}` (cached, rate-limited, with a quota), `GET /reports/documents` (SQL),
  `GET /welcome` (only for `X-Tenant: acme`), `POST /contacts` (built-in formats). Seeded into an empty store only.
- 🛡️ **Validators:** `nip` (C#), `unique-value` (C#, DB lookup), `iban` and `booking-request` (FluentValidation).
- 📝 **Audit log:** the latest changes at `GET /api/admin/endpoints/audit`.
- 👋 **Custom management API:** `Greetings/` builds its own API on the injected `IDynamicEndpointManager`.

```bash
dotnet run --project samples/DynamicEndpoints.AppHost                 # .NET Aspire: 2 replicas, PostgreSQL, Redis, dashboard
docker compose -f samples/docker-compose.yml up --build               # live demo: 2 instances + PostgreSQL + Redis + nginx
```

The Aspire dashboard shows the `dynamic_endpoints.*` metrics per endpoint and the binding, validation and processor spans. More in
[Sample app, Aspire & live demo](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/aspire-and-demo.html).

## 🚢 Releasing

**Every push to `main` is a release.** `.github/workflows/release.yml` builds, tests, packs and publishes to NuGet through
[Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing) (no API key in the repo), then tags the commit and
creates a GitHub Release with generated notes.

| Version part | Comes from |
|---|---|
| `major.minor` | `<VersionPrefix>` in `Directory.Build.props`. Bump it by hand |
| `patch` | the last `vX.Y.*` tag + 1. Automatic, gap-free, resets after a prefix bump |

Pushes that only touch `README.md`, `docs/`, `samples/` or the benchmarks are not released. The documentation site is
published to GitHub Pages by `.github/workflows/docs.yml`.

Release notes come from [`CHANGELOG.md`](CHANGELOG.md). Add entries under **[Unreleased]** together with your change, and the
workflow moves them under the released version. Pushes without entries get auto-generated notes only, and no bot commit.
After a release with entries, run `git pull` before your next commit (the workflow committed the changelog).

## 🛠️ Building & testing

```bash
dotnet build DynamicEndpoints.slnx
dotnet test DynamicEndpoints.slnx
```

```
src/DynamicEndpoints                       core library
src/DynamicEndpoints.Analyzers             Roslyn analyzers (packed into DynamicEndpoints)
src/DynamicEndpoints.AdminUI               admin panel (embedded HTML/JS)
src/DynamicEndpoints.EntityFrameworkCore   EF Core store
src/DynamicEndpoints.FluentValidation      FluentValidation integration
src/DynamicEndpoints.PostgreSql            LISTEN/NOTIFY change notifier
src/DynamicEndpoints.Redis                 Redis pub/sub change notifier
src/DynamicEndpoints.OpenTelemetry         OpenTelemetry registration
src/DynamicEndpoints.Sql                   read-only SQL query processor
src/DynamicEndpoints.Yaml                  YAML text format
src/DynamicEndpoints.Cli                   dynamic-endpoints .NET tool
src/DynamicEndpoints.Testing               test helpers
src/DynamicEndpoints.Templates             dotnet new project template
samples/DynamicEndpoints.Sample            demo app: admin panel, Swagger UI, SQLite (or PostgreSQL), Dockerfile
samples/DynamicEndpoints.AppHost           .NET Aspire: the sample ×2 with PostgreSQL, Redis and the dashboard
samples/DynamicEndpoints.ServiceDefaults   Aspire service defaults incl. the dynamic endpoint metrics
tests/DynamicEndpoints.Tests               integration tests (TestServer + SQLite; PostgreSQL and Redis in Docker)
tests/DynamicEndpoints.Analyzers.Tests     analyzer tests
tests/DynamicEndpoints.Benchmarks          BenchmarkDotNet benchmarks (not run by dotnet test)
docs/                                      documentation site (docfx)
```

Tests marked `[DockerFact]` start PostgreSQL and Redis containers (Testcontainers) and are skipped when Docker isn't available.

## 🗺️ Roadmap

- [x] Runtime endpoints with persistence and multi-instance refresh
- [x] JSON Schema constraints, JsonLogic rules, custom & FluentValidation validators
- [x] OpenAPI 3.1 generation
- [x] Assembly scanning & seeding
- [x] Zero-dependency core (built-in JSON Schema subset & JsonLogic engine)
- [x] Built-in string formats
- [x] Continuous delivery: every push to `main` publishes a new NuGet version
- [x] Draft → publish workflow with version history, diffs, rollback and scheduled publishing
- [x] Instant change propagation: PostgreSQL `LISTEN/NOTIFY` and Redis pub/sub
- [x] Change events, transactional change sets, EF Core migrations, test kit
- [x] Admin UI as a reusable package
- [x] OpenTelemetry metrics and tracing per dynamic endpoint
- [x] Built-in processors, per-endpoint caching and rate limits, GitOps export/import and CLI, OpenAPI import
- [x] Multi-tenancy, audit log, analyzers, project template, documentation site

## ⚖️ License

DynamicEndpoints is licensed under the [MIT License](LICENSE). Use it in commercial projects, no strings attached.

| Package | Depends on |
|---|---|
| `DynamicEndpoints` | ASP.NET Core shared framework only |
| `DynamicEndpoints.AdminUI` | ASP.NET Core shared framework only |
| `DynamicEndpoints.EntityFrameworkCore` | `Microsoft.EntityFrameworkCore.Relational` (MIT) |
| `DynamicEndpoints.FluentValidation` | [FluentValidation](https://github.com/FluentValidation/FluentValidation) (Apache-2.0) |
| `DynamicEndpoints.PostgreSql` | [Npgsql](https://github.com/npgsql/npgsql) (PostgreSQL License) |
| `DynamicEndpoints.Redis` | [StackExchange.Redis](https://github.com/StackExchange/StackExchange.Redis) (MIT) |
| `DynamicEndpoints.OpenTelemetry` | [OpenTelemetry.Api](https://github.com/open-telemetry/opentelemetry-dotnet) (Apache-2.0) |
| `DynamicEndpoints.Sql` | ASP.NET Core shared framework only (bring your ADO.NET provider) |
| `DynamicEndpoints.Yaml` | [YamlDotNet](https://github.com/aaubry/YamlDotNet) (MIT) |
| `DynamicEndpoints.Cli` | `DynamicEndpoints.Yaml` |
| `DynamicEndpoints.Testing` | `Microsoft.AspNetCore.Mvc.Testing` (MIT) |

## 🤝 Contributing

Issues and PRs are welcome. Run `dotnet test` before you push, and keep changes covered by integration tests.

<div align="center">

---

Made with ☕ and a healthy disrespect for recompiling.

**If this saved you a sprint, drop a ⭐**

</div>
