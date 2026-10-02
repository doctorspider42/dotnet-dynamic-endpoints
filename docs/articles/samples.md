# Samples

Small apps in [`samples/`](https://github.com/doctorspider42/dotnet-dynamic-endpoints/tree/main/samples), one feature area each,
easy to read top to bottom. Every one of them runs on its own with `dotnet run` – SQLite, no Docker – and has the
[admin panel](admin-ui.md) at `/admin/` and Swagger UI at `/swagger` to click around. Each folder has a `README.md` (what it
shows, what to click and call) and a `.http` file with the interesting requests for Visual Studio, Rider or VS Code REST Client.

```bash
dotnet run --project samples/01-QuickStart      # → http://localhost:5101/admin/
```

| Feature | Sample | Port | What to look at | Docs |
|---|---|---|---|---|
| The minimum | [`01-QuickStart`](https://github.com/doctorspider42/dotnet-dynamic-endpoints/tree/main/samples/01-QuickStart) | 5101 | `Program.cs` step by step, a typed processor, a seeder – the README quick start and the project template | [Getting started](getting-started.md) |
| Validation | [`02-Validation`](https://github.com/doctorspider42/dotnet-dynamic-endpoints/tree/main/samples/02-Validation) | 5102 | constraints and formats, C# and FluentValidation validators, JsonLogic rules, error codes, `IDynamicErrorResponseFactory`, Polish messages | [Validation](validation.md), [error format](error-responses.md), [localization](localization.md) |
| Your own processors | [`03-CustomProcessors`](https://github.com/doctorspider42/dotnet-dynamic-endpoints/tree/main/samples/03-CustomProcessors) | 5103 | typed processors with configuration, DI and DB access, a filter, file uploads, `SetParsedValue` / `GetParsedValue`, a management API on `IDynamicEndpointManager` | [Processors](processors-and-validators.md), [filters](filters.md), [uploads](file-uploads.md), [handing work on](handing-work-on.md), [manager](managing-endpoints.md) |
| Built-in processors | [`04-BuiltInProcessors`](https://github.com/doctorspider42/dotnet-dynamic-endpoints/tree/main/samples/04-BuiltInProcessors) | 5104 | `http-forward`, `webhook` (signed, retried), `response`, `sql-query` – against a fake upstream in the same app | [Built-in processors](built-in-processors.md) |
| CRUD on EF Core entities | [`05-EfCrud`](https://github.com/doctorspider42/dotnet-dynamic-endpoints/tree/main/samples/05-EfCrud) | 5105 | the allowlist (`Fields`, `ReadOnly`, `Filterable`, `Sortable`, `AllFields`), an interceptor, ETags and `If-Match`, scaffolding, `HandledByCrud<T>` – without tenancy | [ef-crud](ef-crud.md) |
| Multi-tenancy | [`06-MultiTenancy`](https://github.com/doctorspider42/dotnet-dynamic-endpoints/tree/main/samples/06-MultiTenancy) | 5106 | `X-Tenant`, shared and tenant endpoints on one route, the tenant admin API and panel, OpenAPI per tenant, ef-crud with a tenant column, sql-query with `AllowTenants` | [Multi-tenancy](multi-tenancy.md) |
| Import from OpenAPI | [`07-OpenApiImport`](https://github.com/doctorspider42/dotnet-dynamic-endpoints/tree/main/samples/07-OpenApiImport) | 5107 | a bundled petstore in mock mode, imported from code on start; `x-dynamic-endpoints-processor`, a processor per tag, re-import with sync | [OpenAPI import](openapi-import.md) |
| GitOps | [`08-GitOps`](https://github.com/doctorspider42/dotnet-dynamic-endpoints/tree/main/samples/08-GitOps) | 5108 | `endpoints.yaml`, export/import with dry runs, YAML, an API-key protected admin API, `gitops.ps1` / `gitops.sh` with the `dynamic-endpoints` CLI | [Export, import & GitOps](export-import-gitops.md) |
| Drafts, history, audit | [`09-DraftsHistoryAudit`](https://github.com/doctorspider42/dotnet-dynamic-endpoints/tree/main/samples/09-DraftsHistoryAudit) | 5109 | drafts, scheduled publishing, revisions, diffs, rollback, an audit log in your own table | [Drafts & history](drafts-and-history.md), [audit log](audit-log.md) |
| Caching & rate limits | [`10-CachingAndRateLimits`](https://github.com/doctorspider42/dotnet-dynamic-endpoints/tree/main/samples/10-CachingAndRateLimits) | 5110 | `Cache-Control`, ETags/304, output cache, rate limits per IP or API key, quotas, the 429 through the error factory | [Caching & rate limits](caching-and-rate-limits.md) |
| Observability | [`11-Observability`](https://github.com/doctorspider42/dotnet-dynamic-endpoints/tree/main/samples/11-Observability) | 5111 | OpenTelemetry metrics and traces per endpoint with the console exporter, OTLP when configured | [Metrics & tracing](telemetry.md) |
| Everything together | [`Showcase`](https://github.com/doctorspider42/dotnet-dynamic-endpoints/tree/main/samples/Showcase) | 5118 | all features in one app – the app of the Aspire AppHost, docker compose and the live demo container | [Sample app, Aspire & live demo](aspire-and-demo.md) |

## How they are built

- **Self-contained.** Each `Program.cs` reads top to bottom with a comment per step; there is no shared project. The parts that
  repeat – the `DbContext` with `ApplyDynamicEndpointsConfiguration()`, `EnsureCreatedAsync()`, the admin API, the panel and
  Swagger UI – are a dozen lines each, so every app shows the whole setup.
- **Their own database.** Each sample has a SQLite file of its own (`quickstart.db`, `validation.db`, …, from the
  `ConnectionStrings:Default` of its `appsettings.json`), created on start in the working directory – the sample's folder with
  `dotnet run`. The seeders only seed an empty store: delete the file to start over. A real application uses EF Core
  migrations instead of `EnsureCreated` ([EF Core & migrations](ef-core.md)).
- **Open admin APIs.** The admin API and the panel are open, to click around. Put `.RequireAuthorization(…)` on both in real life
  ([Security](security.md)); `08-GitOps` shows a second admin API behind an API key for CI.
- **Tested.** `tests/DynamicEndpoints.Samples.Tests` starts every sample on TestServer with a temporary SQLite file and calls
  the endpoints its README documents.
