# Sample app, Aspire & live demo

## The sample

```bash
dotnet run --project samples/Showcase
```

- 🖥️ **Admin panel:** `http://localhost:5118/admin/`, from the [`DynamicEndpoints.AdminUI`](admin-ui.md) package. Editor with
  processor forms, caching and rate limits, drafts and scheduled publishing, history with diffs and rollback, export/import,
  the OpenAPI import wizard, the audit log, and a "Try" console with curl / HTTPie / C# snippets.
- 🏢 **Tenants:** `UseMultiTenancy(t => t.FromHeader("X-Tenant"))`. The panel at `/admin/` manages every tenant; acme's own panel
  is at `/admin/tenants/acme/`, on the tenant admin API `/api/admin/tenants/{tenant}/endpoints` (both open – in real life secure
  them with a policy that checks the user belongs to the tenant). OpenAPI per tenant at `/openapi/{tenant}/dynamic.json`.
- 📜 **Swagger UI:** `http://localhost:5118/swagger`, with the *Dynamic endpoints* and *Admin API* documents.
- 🧩 **Processors:** `echo`, `template`, `calculator`, `collection` (a JSON document store in SQLite) and an inline `clock`, plus the
  [built-in](built-in-processors.md) `http-forward`, `webhook`, `response` and `sql-query` (on the sample's own database).
- 🛡️ **Validators:** `nip` (C#), `unique-value` (C#, DB lookup), `iban` and `booking-request` (FluentValidation). `POST /contacts` shows the built-in formats.
- 🔁 **YAML:** `AddYamlFormat()`, so export and import speak YAML in the panel, the admin API and the CLI.
- ⏱️ **Caching & rate limits:** `AddRateLimiter()` / `UseRateLimiter()` and `AddOutputCache()` / `UseOutputCache()` are always on.
- 🌱 **Seeding:** `SampleEndpointsSeeder` seeds the demo endpoints when the store is empty, among them:

| Endpoint | Shows |
|---|---|
| `GET /products/{sku}` | the `response` processor with caching (60 s, ETag/304, 30 s output cache), 5 requests per minute and a quota of 1000 a day – call it a few times |
| `GET /reports/documents?collection=notes` | `sql-query`: a read-only, parameterized query over the documents of the `collection` processor |
| `GET /welcome?name=Ann` | an endpoint of tenant `acme`: send `X-Tenant: acme`, without it it's a `404` |

- 📝 **Audit log:** changes are logged and the latest ones are at `GET /api/admin/endpoints/audit` ([audit log](audit-log.md)).
- 👋 **Custom management API:** `Greetings/` builds its own API on the injected `IDynamicEndpointManager`.

It runs on SQLite by default. With a `dynamicendpoints` connection string it uses PostgreSQL, and with a `redis` connection
string it propagates changes through Redis ([multiple instances](multiple-instances.md)).

> [!NOTE]
> The seeder only seeds an empty store. If you ran an older version of the sample, delete its SQLite database
> (`dynamic-endpoints.db` in the directory you started it from) to get the new demo endpoints.

## …with .NET Aspire

```bash
dotnet run --project samples/DynamicEndpoints.AppHost      # needs Docker or Podman
```

The AppHost runs the sample in **two replicas** on **PostgreSQL** (definitions, history, drafts) with **Redis** change
notifications: change an endpoint in the panel and both replicas serve it at once. `DynamicEndpoints.ServiceDefaults` wires
OpenTelemetry with `AddDynamicEndpointsInstrumentation()` ([telemetry](telemetry.md)), so the Aspire dashboard shows
`dynamic_endpoints.requests`, `…validation.failures` by layer and `…processor.duration` per endpoint, and traces with the
binding, validation and processor spans.

## …as a container (live demo)

```bash
docker build -f samples/Showcase/Dockerfile -t dynamic-endpoints-showcase .
docker run --rm -p 8080:8080 dynamic-endpoints-showcase               # SQLite in /data
docker compose -f samples/docker-compose.yml up --build              # 2 instances + PostgreSQL + Redis + nginx
```

The image runs in **demo mode** (`Demo__Enabled`): a rate limit per client IP (`Demo__RequestsPerMinute`, 120) and a reset to
the seeded endpoints every hour (`Demo__ResetInterval`). The admin API stays open, so don't put anything private in there.
`/health` and `/alive` are there for the platform's probes, and `OTEL_EXPORTER_OTLP_ENDPOINT` turns on telemetry export.
For a hosted demo, use the same image with a managed PostgreSQL and Redis (`ConnectionStrings__dynamicendpoints`,
`ConnectionStrings__redis`).
