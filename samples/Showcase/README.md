# Showcase – everything together

The combined demo: one app with every feature switched on at once – processors and validators of its own, the built-in
processors, ef-crud, multi-tenancy, caching and rate limits, drafts and history, the audit log, YAML, OpenTelemetry. It is the
app behind the [.NET Aspire AppHost](../DynamicEndpoints.AppHost/AppHost.cs) (two replicas on PostgreSQL with Redis change
notifications), [`docker-compose.yml`](../docker-compose.yml) and the [`Dockerfile`](Dockerfile) of the live demo.

**To learn a feature, start with the focused samples** – small apps that read top to bottom, one feature area each, all with
SQLite, the admin panel and Swagger UI:

| | Sample | |
|---|---|---|
| 01 | [Quick start](../01-QuickStart/README.md) | the minimum setup |
| 02 | [Validation](../02-Validation/README.md) | four layers, error codes, one error format, Polish messages |
| 03 | [Custom processors](../03-CustomProcessors/README.md) | typed processors, DI, filters, uploads, handing work on, a management API |
| 04 | [Built-in processors](../04-BuiltInProcessors/README.md) | http-forward, webhook, response, sql-query |
| 05 | [ef-crud](../05-EfCrud/README.md) | CRUD on EF Core entities, without tenancy |
| 06 | [Multi-tenancy](../06-MultiTenancy/README.md) | tenants, their admin APIs, panels, OpenAPI, rows and SQL connections |
| 07 | [OpenAPI import](../07-OpenApiImport/README.md) | mock mode, processors per operation and tag, re-import with sync |
| 08 | [GitOps](../08-GitOps/README.md) | export/import, YAML, dry runs, the CLI |
| 09 | [Drafts, history & audit](../09-DraftsHistoryAudit/README.md) | drafts, scheduled publishing, diffs, rollback, audit log |
| 10 | [Caching & rate limits](../10-CachingAndRateLimits/README.md) | Cache-Control, ETags, output cache, limits, quotas, 429 |
| 11 | [Observability](../11-Observability/README.md) | OpenTelemetry metrics and traces with a console exporter |

## Run it

```bash
dotnet run --project samples/Showcase                     # SQLite – http://localhost:5118/admin/
dotnet run --project samples/DynamicEndpoints.AppHost     # .NET Aspire: 2 replicas, PostgreSQL, Redis, dashboard (Docker or Podman)
docker compose -f samples/docker-compose.yml up --build   # live demo: 2 instances + PostgreSQL + Redis + nginx → http://localhost:8080/admin/
```

- Admin panel at `/admin/`, acme's own panel at `/admin/tenants/acme/`, Swagger UI at `/swagger`.
- SQLite (`dynamic-endpoints.db`) by default; PostgreSQL with a `dynamicendpoints` connection string, Redis change notifications
  with a `redis` connection string. Both come from the AppHost and docker compose.
- `DemoMode` (`Demo__Enabled=true`, set by the `Dockerfile`): a rate limit per client IP and a reset to the seeded endpoints
  every hour.
- The seeder (`SampleEndpointsSeeder.cs`) only seeds an empty store – delete the database to get the current demo endpoints.

More in [Sample app, Aspire & live demo](../../docs/articles/aspire-and-demo.md). The smoke tests of the showcase are in
`tests/DynamicEndpoints.Tests` (`PersistenceTests`, `TestingPackageTests`); those of the focused samples in
`tests/DynamicEndpoints.Samples.Tests`.
