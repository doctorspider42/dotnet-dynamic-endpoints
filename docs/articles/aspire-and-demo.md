# Sample app, Aspire & live demo

## The sample

```bash
dotnet run --project samples/DynamicEndpoints.Sample
```

- 🖥️ **Admin panel:** `http://localhost:5118/admin/`, from the [`DynamicEndpoints.AdminUI`](admin-ui.md) package. List, editor
  (parameters, constraints, rules, validators, processor config), drafts and scheduled publishing, history with diffs and
  rollback, and a "Try" console with generated examples and curl / HTTPie / C# snippets.
- 📜 **Swagger UI:** `http://localhost:5118/swagger`, with the *Dynamic endpoints* and *Admin API* documents.
- 🧩 **Processors:** `echo`, `template`, `calculator`, `collection` (a JSON document store in SQLite) and an inline `clock`.
- 🛡️ **Validators:** `nip` (C#), `unique-value` (C#, DB lookup), `iban` and `booking-request` (FluentValidation). `POST /contacts` shows the built-in formats.
- 🌱 **Seeding:** `SampleEndpointsSeeder` seeds the demo endpoints on the first start.
- 📝 **Audit log:** changes are logged and the latest ones are at `GET /api/admin/endpoints/audit` ([audit log](audit-log.md)).
- 👋 **Custom management API:** `Greetings/` builds its own API on the injected `IDynamicEndpointManager`.

It runs on SQLite by default. With a `dynamicendpoints` connection string it uses PostgreSQL, and with a `redis` connection
string it propagates changes through Redis ([multiple instances](multiple-instances.md)).

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
docker build -f samples/DynamicEndpoints.Sample/Dockerfile -t dynamic-endpoints-sample .
docker run --rm -p 8080:8080 dynamic-endpoints-sample                # SQLite in /data
docker compose -f samples/docker-compose.yml up --build              # 2 instances + PostgreSQL + Redis + nginx
```

The image runs in **demo mode** (`Demo__Enabled`): a rate limit per client IP (`Demo__RequestsPerMinute`, 120) and a reset to
the seeded endpoints every hour (`Demo__ResetInterval`). The admin API stays open, so don't put anything private in there.
`/health` and `/alive` are there for the platform's probes, and `OTEL_EXPORTER_OTLP_ENDPOINT` turns on telemetry export.
For a hosted demo, use the same image with a managed PostgreSQL and Redis (`ConnectionStrings__dynamicendpoints`,
`ConnectionStrings__redis`).
