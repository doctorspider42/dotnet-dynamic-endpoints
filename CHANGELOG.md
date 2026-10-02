# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project uses [Semantic Versioning](https://semver.org/).

> **How releases work:** every push to `main` publishes a new version (see [Releasing](README.md#-releasing)).
> Add your entries under **[Unreleased]** together with the change. The release workflow moves them under the new version,
> and they become the GitHub Release notes. Pushes without entries are released with auto-generated notes only.

## [Unreleased]

### Added

- **Metrics and tracing per dynamic endpoint**, with `System.Diagnostics` only (the core still has no dependencies). The meter
  `DynamicEndpoints` has `dynamic_endpoints.requests` (with outcome and status code), `dynamic_endpoints.request.duration`,
  `dynamic_endpoints.validation.failures` (by layer: binding, constraints, parameter validators, rules, request validators),
  `dynamic_endpoints.processor.duration` and `dynamic_endpoints.errors`, all tagged with the endpoint's id, name, processor,
  route and method. The activity source `DynamicEndpoints` adds spans for the request, filters, binding, every validation layer
  and the processor. Names are constants in `DynamicEndpointsTelemetry`.
- **Drafts, history and rollback.** `SaveDraftAsync` saves a validated change (or a new endpoint) that isn't routed until
  `PublishAsync`, or until its `PublishAt` time (`options.ScheduledPublishInterval`, 10 s by default; `PublishDueAsync()` for your own
  scheduler). Every change is kept as a revision with its kind and comment: `GetHistoryAsync`, `GetRevisionAsync`,
  `DiffAsync(id, from, to)`, `DiffDraftAsync` and `RollbackAsync(id, revision)`, which publishes old content as the next revision.
  Publishing an outdated draft fails with a concurrency error. `DynamicEndpointDiff.Compare` compares any two definitions. Change sets
  support drafts, publishing and rollbacks too. The admin API has `/drafts`, `/{id}/draft`, `/{id}/publish`, `/{id}/revisions`,
  `/{id}/diff` and `/{id}/revisions/{revision}/rollback`.
- `IDynamicEndpointRevisionStore`: the optional store capability behind it, implemented by the in-memory store (and so by
  `DynamicEndpoints.Testing`) and the EF Core store. The bundled `DynamicEndpointsDbContext` gets the migration
  `DynamicEndpointRevisionsAndDrafts` (provider-independent, adopted for databases created with `EnsureCreated`).
- **Admin panel as a package.** `DynamicEndpoints.AdminUI` serves the panel from embedded files:
  `app.MapDynamicEndpointsAdminUI("/admin", adminApiPath: "/api/admin/endpoints")` returns a `RouteGroupBuilder`, so
  `.RequireAuthorization()` works. It reserves its prefix, follows the path base and takes a title and Swagger / OpenAPI links.
  Besides the list and the editor, it has drafts with scheduled publishing, history with diffs and rollback, and the "Try"
  console generates example values from the parameters (examples, defaults, allowed values, formats, lengths, ranges, JSON
  Schemas) and copies requests as curl, HTTPie or C# `HttpClient` code. The sample uses the package instead of its own `wwwroot`.
- `DynamicEndpoints.OpenTelemetry` package: `AddDynamicEndpointsInstrumentation()` for `MeterProviderBuilder` and `TracerProviderBuilder`.
- **.NET Aspire sample.** `samples/DynamicEndpoints.AppHost` runs the sample in two replicas on PostgreSQL with Redis change
  notifications, and `samples/DynamicEndpoints.ServiceDefaults` sends the per-endpoint metrics and traces to the Aspire dashboard.
  The sample picks PostgreSQL and Redis up from the `dynamicendpoints` and `redis` connection strings.
- **Container for a live demo.** `samples/DynamicEndpoints.Sample/Dockerfile` and `samples/docker-compose.yml` (two instances,
  PostgreSQL, Redis, nginx). A demo mode (`Demo__Enabled`) adds a rate limit per client and resets the endpoints every hour.
- **Request snippets.** `GET /{id}/snippets` of the admin API (and `POST /snippets` for an unsaved definition) returns an example
  request built from parameter examples, defaults, allowed values and constraints, plus ready-made curl, HTTPie and C# `HttpClient`
  snippets. Required documented headers and the credentials of the security schemes appear as placeholders. In code:
  `IDynamicEndpointSnippetGenerator`.
- **Export and import of definitions.** `IDynamicEndpointTransfer` and the admin API's `GET /export` (all, or `?id=…`) write a stable,
  diff-friendly format (`dynamic-endpoints/v1`: sorted, no revisions or timestamps, `\n` line endings). `POST /import` reads it with
  `?mode=create|upsert|sync` (`sync` also deletes what is missing) and `?dryRun=true`, which reports per endpoint what would be
  created, updated (with the changed properties), deleted, left unchanged or skipped. Endpoints are matched by id, or by method and
  route when the file has none. The whole import is validated first, including route conflicts within the file; when anything is
  invalid nothing is written and the response is `422`.
- **`DynamicEndpoints.Cli`**, a new .NET tool (`dynamic-endpoints`) for GitOps through the admin API: `list`, `export` (JSON or
  YAML by file extension), `push`/`import` (`--mode create|upsert|sync`, `--sync`, `--dry-run`), `diff` and `import-openapi`.
  Credentials via `--api-key`/`--api-key-header`, `--token`, `-H` or `DYNAMIC_ENDPOINTS_*` environment variables; exit codes
  0 (success), 1 (rejected, nothing written), 2 (`diff` found differences), 3 (usage), 4 (connection/HTTP error); `--json` output.
- **Import from OpenAPI.** `IDynamicEndpointOpenApiImporter` and `POST /import/openapi` turn an OpenAPI 3.x document (JSON, or
  YAML with `DynamicEndpoints.Yaml`) into endpoint skeletons: routes and methods, path/query/header parameters with types, formats
  and constraints, JSON and form body properties (`allOf` merged, `$ref`s resolved, files with content types), response schemas
  and examples, and `requireAuthorization` from `security`. A dry run reports what would be created and, per operation, what
  couldn't be mapped. Imported endpoints are disabled by default, existing routes are skipped, and options set the processor,
  a route prefix, the group and a tag filter.
- **Text formats.** `IDynamicEndpointsTextFormat` – JSON built in; the admin API picks it by `Content-Type`, `?format=` or `Accept`.
- **`DynamicEndpoints.Yaml`**, a new package: `AddYamlFormat()` adds YAML to export, import and the OpenAPI import;
  `DynamicEndpointsYaml.Parse`/`Write` convert between YAML and `JsonNode`.
- **Built-in processors** (opt-in, typed configuration validated on save): `AddBuiltInProcessors()` or one by one:
  - `http-forward` (`AddHttpForwardProcessor()`): forwards to another service through `IHttpClientFactory` with a URL template
    (values URL-encoded, no placeholders in the host), headers with `{config:…}` secrets, forwarded request headers, body modes or a
    body template, timeout, and the upstream response relayed or mapped by a `responseTemplate`. `502`/`504` on upstream failures.
  - `webhook` (`AddWebhookProcessor()`): JSON webhooks with retries and exponential back-off (`Retry-After` honoured), HMAC-SHA256
    signatures from a configuration secret, a stable `X-Webhook-Delivery` id, and optional background delivery.
  - `response` (`AddResponseTemplateProcessor()`): responses rendered from JSON or text templates (`{{name}}`, `{{a.b[0]}}`).
  - `DynamicHttpProcessorOptions`: named HTTP client, `AllowedHosts` against SSRF (checked on save and per call), response size limit.
- **`DynamicEndpoints.Sql`**, a new package: `AddSqlQueryProcessor(…)` registers `sql-query`, a read-only SQL processor for any
  ADO.NET provider. Request values are always bound as parameters; single `SELECT`/`WITH` statements only, data-changing keywords
  rejected on save and before every run, every query in a rolled-back transaction. See the package README for the security notes.
- **Response caching per endpoint.** `Caching` in the definition (`.Cached(…)` / `.WithCaching(…)`): `Cache-Control` max age and
  visibility (private by default for endpoints that require authorization), `noStore`, ETags with `304 Not Modified`, and
  server-side output caching through ASP.NET Core `OutputCache` (`outputCacheSeconds`, `outputCachePolicy`, `varyByQuery`,
  `varyByHeader`; header parameters are always part of the key). Cached entries are evicted on every instance when the definition
  changes. Documented in OpenAPI (`Cache-Control`/`ETag` headers, `If-None-Match`, `304`).
- **Rate limits and quotas in the definition.** `RateLimit` (`.RateLimited(…)` / `.WithRateLimit(…)`): fixed window, sliding window,
  token bucket or concurrency limits, partitioned by IP address, user, a header such as an API key, or per endpoint, with an
  optional long-term quota (hour/day/week/month). Built on ASP.NET Core rate limiting (`AddRateLimiter()` + `UseRateLimiter()`);
  counters start over when the limit settings change and survive reloads. Rejections are `429` with `Retry-After`, formatted by
  `IDynamicErrorResponseFactory` (new `DynamicErrorKind.TooManyRequests`, also used for empty 429 responses), and documented in OpenAPI.

### Changed

- `IDynamicEndpointManager` has new members for drafts, history and rollback. Your own implementations of the interface (e.g.
  decorators) need them too.
- **EF Core, your own DbContext:** `ApplyDynamicEndpointsConfiguration()` also maps `DynamicEndpointRevisions` and
  `DynamicEndpointDrafts`. Add a migration for them, or pass `history: false` to keep the 0.3 model without history and drafts.
  Contexts that map only `DynamicEndpointRecordConfiguration` keep working unchanged.
- `ListAsync` and `GetAsync` also return never-published drafts, with the new status `DynamicEndpointStatus.Draft`.
  `DynamicEndpointState.Draft` carries the draft of an endpoint.
- The admin API answers `501` when the store keeps no history and drafts.
- `DynamicEndpoints.Testing`: scheduled publishing is off in the in-memory setup, so call `PublishDueAsync()` in tests.

## [0.3.0] - 2026-10-02

### Added

- **Instant propagation across instances.** After a change, the instance publishes it through an `IDynamicEndpointChangeNotifier`,
  and the other instances reload right away. Bursts are coalesced, and listeners reload after every reconnect. Polling
  (`RefreshInterval`) stays as the fallback. New packages:
  - `DynamicEndpoints.PostgreSql`: `UsePostgreSqlChangeNotifications(connectionString)`, through `LISTEN/NOTIFY` with a keep-alive
    check of the listening connection.
  - `DynamicEndpoints.Redis`: `UseRedisChangeNotifications("redis:6379")`, through pub/sub. It can also reuse the `IConnectionMultiplexer` from DI.
  - Your own transport: `UseChangeNotifier<T>()`. `options.InstanceId` identifies the sender.
- **Change events.** `AddChangeHandler<T>()` / `OnChanged((change, ct) => …)` run after endpoints were created, updated (including
  enable/disable) or deleted, with the definitions before and after. `Origin` is `Local` once, on the instance that made the change
  (for audit logs), and `Remote` on every instance that picked it up by a reload (for cache invalidation).
- **Upsert.** `IDynamicEndpointManager.UpsertAsync` creates an endpoint, or replaces the stored one with the same `Id` without a
  revision check. When the content didn't change, nothing is written and the revision stays.
- **Change sets in your own transaction.** `manager.BeginChanges(store)` stages creates, updates, upserts, deletes and enable/disable
  through the given store, and `ApplyAsync()` updates the routing table after your commit. With EF Core,
  `db.GetDynamicEndpointStore()` only tracks the writes, so your own `SaveChanges` stores the endpoint and your data atomically.
  `BeginChanges(HttpContext.RequestServices)` shares the scoped DbContext and its transaction instead. Route conflicts are checked
  across the whole set.
- **EF Core migrations.** The bundled `DynamicEndpointsDbContext` ships provider-independent migrations.
  `UseEntityFrameworkStore(…, migrateOnStartup: true)` applies them on start-up and adopts tables created earlier with `EnsureCreated`.
  For your own context there's `DynamicEndpointRecordConfiguration` (an `IEntityTypeConfiguration`) and `MigrateOnStartup<TContext>()`.
  `IDynamicEndpointStoreInitializer` / `AddStoreInitializer<T>()` run any preparation before definitions are loaded.
- **One error format.** `IDynamicErrorResponseFactory` (`UseErrorResponseFactory<T>()` or `UseErrorResponses(context => …)`) builds
  every error response: validation, invalid body, 413 and 415. The context has `Kind`, `StatusCode`, localized `Title`, `Errors` with
  codes, `Endpoint` and `RequestId`. With `app.UseDynamicEndpointsErrorResponses()` the same factory also formats empty
  401/403/404/405 responses (also for static endpoints and unknown routes) and unhandled exceptions (500, logged, never exposed).
  This replaces `UseStatusCodePages`. Filters still run afterwards and can override single responses.
- **Handing work on.** Validators call `context.SetParsedValue(value)`, and processors read it with `request.GetParsedValue<T>(name)`.
  `Items` is a per-request bag shared by filters, validators and the processor.
- `HttpContext.GetDynamicEndpoint()` returns the endpoint's metadata with its complete definition, for your own middleware.
  `DynamicValidationContext.Metadata` returns the same.
- **OpenAPI:**
  - Security requirements are attached to each operation, and `AllowAnonymous` endpoints get none. `AddSecurityScheme` and `AddApiKey`
    take an `appliesTo` filter.
  - Request examples are composed from parameter examples, or set with `RequestExample` / `WithRequestExample(…)`. Responses take
    `ResponseExample` / `WithResponseExample(…)`.
  - Parameters and common headers (`AddHeader(…, example: …)`) carry `example`.
- `DynamicEndpoints.Testing`: `WebApplicationFactory<T>.WithInMemoryDynamicEndpoints(…)` / `services.UseInMemoryDynamicEndpoints()`
  swap the store for an in-memory one and drop migrations, notifiers and polling. `AddDynamicEndpointAsync(…)` adds an endpoint.
  `DynamicEndpointsTestServer` runs endpoints without your application, and `InMemoryDynamicEndpointChangeNotifier` lets you test
  several instances.

### Changed

- `AddSecurityScheme` / `AddApiKey` attach their requirement to every operation (`OpenApi.OperationSecurity`) instead of adding a
  document-level `security` entry. Requirements you add to `OpenApi.SecurityRequirements` yourself are still document-level, and
  anonymous endpoints now opt out of them with `security: []`.
- Operations with a security requirement document a `401` response.
- `IDynamicEndpointManager` has the new members `UpsertAsync` and `BeginChanges`. Your own implementations of the interface (e.g.
  decorators) need them too.

## [0.2.0] - 2026-10-01

### Added

- **File uploads and forms.** `Form` parameters read `multipart/form-data` and `application/x-www-form-urlencoded` bodies, and the new
  `File` type (single or `Array` of files) binds uploads without base64. Limits: `MaxFileSize`, `AllowedContentTypes` (with `image/*`
  wildcards) and `MaxFormBodySize` for the whole form (30 MB by default). Processors and validators read the content with
  `GetFile(name)` / `GetFiles(name)`, while `Parameters` and JsonLogic rules see `{ fileName, contentType, length }`.
  OpenAPI documents the body as `multipart/form-data` with `format: binary` fields. Fluent API: `FromForm(...)`, `p.File(...)`, `p.Files(...)`.
- **Filters** (`IDynamicEndpointFilter`, `AddFilter<T>()` or inline `AddFilter(onRequest, onValidationFailed)`):
  - `OnRequestAsync` runs after routing and authorization, before the request is read or validated. Use it for access checks such as
    tenant features, and short-circuit with your own result, e.g. a 403.
  - `OnValidationFailedAsync` runs for every rejected request (validation errors, malformed or undecodable bodies, 413, 415). It can
    replace the default `ValidationProblem` with your own error format, and since it runs for every rejection it can also log and
    meter requests that never reach the processor.
- **Error codes.** Every validation error carries a stable code (`required`, `minLength`, `format`, `fileSize`, …, see
  `DynamicValidationCodes`). Rules take an optional `Code`, and custom validators can call `AddError(path, message, code)`.
- **Localized validation messages.** English and Polish are built in, with plural forms. `options.Messages` picks the language
  (`DefaultCulture`, or `UseRequestCulture` together with `UseRequestLocalization()`), overrides single texts (`Set(culture, key, template)`)
  and can route everything through your own localizer (`Localizer`).
- **Richer endpoint metadata.** `DynamicEndpointMetadata` now carries the complete compiled definition (`Definition`, `ProcessorName`,
  `Parameters`, `FindParameter`, `HasJsonBody`, `HasFormBody`, `HasFiles`), so middleware doesn't need to query the store. It's also
  available as `DynamicRequest.Metadata` and on the filter contexts.
- **OpenAPI extensions.** `options.OpenApi.AddApiKey(...)` / `AddSecurityScheme(...)`, document-wide `SecurityRequirements`, common headers
  with `AddHeader(name, description, required, appliesTo)`, and `ConfigureOperation` / `ConfigureDocument` hooks.
- `options.RequiredRoutePrefixes`: every route must start with one of the prefixes, e.g. `/api/v{version:int}`. Checked on save.

### Changed

- **Breaking:** `DynamicEndpointDefinition.Version` is now `Revision`, because it's an optimistic concurrency token and not an API version.
  JSON uses `revision`, and `version` is still accepted on input, so 0.1 clients and stored definitions keep working. The old C# property,
  `DynamicEndpointConcurrencyException.ExpectedVersion` / `ActualVersion` and `DynamicEndpointMetadata.Version` remain as `[Obsolete]`
  aliases. The OpenAPI extension `x-dynamic-endpoint.version` is now `revision`. The EF Core column keeps its name, so no migration is needed.
- **Breaking:** `DynamicEndpointMetadata` is a class with a richer surface instead of the `(Id, Version, Name)` record, and
  `IDynamicEndpointStore.UpdateAsync` names its parameter `expectedRevision`.
- The default validation response's `title` is localized together with the error messages.

### Fixed

- A JSON body with invalid UTF-8, or with escapes that aren't valid text (e.g. a lone `\ud800`), is now rejected with a `400`
  (`body`: "The request body is not valid UTF-8.") instead of failing with a `500` inside the schema validator.

## [0.1.2] - 2026-10-01

### Added

- Every NuGet package now ships its own README, shown on nuget.org.

### Changed

- Pushes that only touch `README.md`, `docs/` or `samples/` no longer publish a new version.

## [0.1.1] - 2026-10-01

### Added

- `CHANGELOG.md`. Release notes for GitHub Releases are taken from the `[Unreleased]` section, which the release workflow then moves under the new version.

## [0.1.0] - 2026-10-01

First public release.

### Added

- Runtime-defined HTTP endpoints: a dynamic `EndpointDataSource` that adds, changes, disables and removes routes without a restart, swapping the routing table atomically.
- `IDynamicEndpointManager` (create, update with optimistic concurrency, enable/disable, delete, dry-run validation, reload) and a fluent `DynamicEndpoint` builder, including type-based `HandledBy<T>()` / `ValidatedBy<T>()`.
- Declarative parameter binding from route, query, headers and JSON body, with type conversion, defaults and request names (`X-Tenant-Id` → `tenantId`).
- Validation in four layers:
  - JSON Schema constraints (a built-in 2020-12 subset; unsupported keywords are rejected on save),
  - built-in formats (e-mail, URI, phone, IPv4/IPv6, time),
  - JsonLogic business rules (a built-in evaluator),
  - custom validators for parameters and whole requests, checked for type compatibility on save.
- Processors (`IDynamicEndpointProcessor`, `DynamicEndpointProcessor<TConfig>`) with strictly validated configuration.
- Seeders (`IDynamicEndpointSeeder`) and assembly scanning (`AddFromAssemblyContaining<T>()`).
- Admin REST API (`MapDynamicEndpointsAdmin`) and an OpenAPI 3.1 document of the active endpoints (`MapDynamicEndpointsOpenApi`).
- Route conflict detection, reserved prefixes, ReDoS-safe patterns and request size/depth limits.
- Multi-instance support: polling refresh and `ReloadAsync()` for push-based propagation.
- `DynamicEndpoints.EntityFrameworkCore`: persistence with a database concurrency token.
- `DynamicEndpoints.FluentValidation`: FluentValidation validators as request- or parameter-level validators.
- Sample app with an admin panel, Swagger UI and SQLite, plus continuous delivery to NuGet through Trusted Publishing.

[Unreleased]: https://github.com/doctorspider42/dotnet-dynamic-endpoints/compare/v0.3.0...HEAD
[0.3.0]: https://github.com/doctorspider42/dotnet-dynamic-endpoints/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/doctorspider42/dotnet-dynamic-endpoints/compare/v0.1.2...v0.2.0
[0.1.2]: https://github.com/doctorspider42/dotnet-dynamic-endpoints/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/doctorspider42/dotnet-dynamic-endpoints/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/doctorspider42/dotnet-dynamic-endpoints/releases/tag/v0.1.0
