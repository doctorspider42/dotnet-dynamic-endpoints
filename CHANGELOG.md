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
- **Container for a live demo.** `samples/Showcase/Dockerfile` and `samples/docker-compose.yml` (two instances,
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
- **OpenAPI import: a processor per operation.** `x-dynamic-endpoints-processor` and `x-dynamic-endpoints-processor-config` on an
  operation, a path item or the document root, and `OpenApiImportOptions.ProcessorsByTag` (`?processorByTag=tag:processor`,
  `--processor-by-tag tag=processor`). Precedence: operation extension > path extension > tag > document extension >
  `Processor` > `DefaultProcessor`; every operation in the result reports its `processor`, `processorSource` and `processorReason`.
  `POST /import/openapi` also accepts `{ "document": …, "options": … }` for configurations, and the CLI `--options <file>`.
- **OpenAPI import: mock mode.** `Mock = true` (`?mock=true`, `--mock`, a checkbox in the wizard) gives every operation the
  built-in `response` processor answering with the status code and example of its first 2xx response – or a body generated from
  its schema like the request snippets do (`allOf` merged, first `oneOf`/`anyOf` alternative); no 2xx: the documented status or
  `204`. Extensions still win. Without `AddResponseTemplateProcessor()` the import says how to register it.
- **OpenAPI import: re-import and sync.** `Mode` (`?mode=`, `--mode`, a selector in the wizard): `create` (default, as before),
  `upsert` updates endpoints imported from the same operation, `sync` also deletes the document's endpoints whose operation is gone.
  Imported definitions record a new optional `DynamicEndpointDefinition.Origin` (`{ kind: openapi, document, operation }`, stored
  in the definition JSON – no schema change); matching is by origin, then by method and route within the document. Hand-made
  endpoints are never updated or deleted. Updates keep the enabled state, tenant, rules, validators, policies, rate limit, caching
  and – unless the import chose one – the processor. The dry run shows `Create`/`Update` (with the changed properties)/`Unchanged`/
  `Delete`/`Skip` (with a reason) per operation; validation is all-or-nothing through `IDynamicEndpointTransfer`, and a tenant's
  admin API only touches the tenant's endpoints. `OpenApiImportOptions.DocumentId` overrides the document id (`info.title`).
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
  rejected on save and before every run, every query in a rolled-back transaction. With multi-tenancy, a tenant's endpoints can
  only use connections assigned to it with `o.AllowTenants(connection, tenants…)` (`ConnectionTenants`), checked on save and before
  every run, so a tenant's admin can't query the application's or another tenant's database. See the package README for the
  security notes.
- `IDynamicEndpointProcessor.ValidateConfiguration(configuration, definition)` and `DynamicEndpointProcessor<T>.Validate(config, definition)`:
  configuration checks that depend on the definition, e.g. its tenant. Default implementations call the existing overloads.
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
- **Multi-tenancy.** `UseMultiTenancy(t => t.FromHeader("X-Tenant-Id"))` lets endpoints belong to a tenant
  (`DynamicEndpointDefinition.Tenant`; `null` is a shared endpoint for every tenant). Routing serves each request the endpoints of
  its tenant plus the shared ones, so several tenants can use the same route; conflicts are checked per tenant.
  - Resolvers: `FromHeader`, `FromHost` (`acme.example.com` → `acme`), `FromClaim` (authenticates during routing when needed),
    `FromRoutePrefix("/tenants/{tenant}")` (every dynamic endpoint is routed under the prefix), or your own
    `IDynamicEndpointTenantResolver`. The first match wins; `HttpContext.GetDynamicEndpointTenantAsync()` and `DynamicRequest.Tenant`
    return the result.
  - Isolation: `manager.ForTenant(tenant)` and `store.ForTenant(tenant)` see and change only that tenant's endpoints and assign new
    ones to it, change sets included.
  - Admin API: `GET /?tenant=acme` and `GET /tenants`; `MapDynamicEndpointsTenantAdmin("/admin/tenants/{tenant}/endpoints")` maps a
    tenant's own admin API.
  - OpenAPI: `IDynamicOpenApiDocumentProvider.GetDocument(tenant)`, and `MapDynamicEndpointsOpenApi("/openapi/{tenant}/dynamic.json")`
    serves one document per tenant.
  - The tenant is stored in the serialized definition, so existing databases need no migration.
- **Audit log.** `AddAuditLog()` records every change made through this instance's manager (change events with origin `Local`, so
  once per change): who (the user of the HTTP request), what, when, and a property-by-property diff
  (`parameters[0].maxLength: 5 → 10`), optionally with the complete definitions before and after.
  - Sinks: `ILogger` by default, `a.ToMemory()` (`InMemoryDynamicEndpointAuditLog`, for tests and development), your own
    `IDynamicEndpointAuditSink` with `a.To<T>()`, and in `DynamicEndpoints.EntityFrameworkCore` `a.ToEntityFramework<TContext>()` – a
    table in your own context, added with `modelBuilder.ApplyDynamicEndpointsAuditConfiguration()`. The bundled
    `DynamicEndpointsDbContext` is unchanged.
  - Queryable sinks (`IDynamicEndpointAuditLog`) are served by the admin API: `GET /audit?endpointId=&tenant=&user=&from=&to=&limit=`
    and `GET /{id}/audit`. A tenant's admin API returns only its own entries.
- **Roslyn analyzers**, shipped inside the `DynamicEndpoints` package (no extra dependency). `DE0001`–`DE0010` report at build time
  what would otherwise only fail on save or at start-up: abstract processor/validator types in `HandledBy<T>()` / `ValidatedBy<T>()` /
  `AddProcessor<T>()`, duplicate processor and validator names, a configuration of the wrong class for a typed processor or validator,
  and invalid literal route templates, `Pattern(…)` regexes (including constructs `NonBacktracking` rejects) and `WithRule(…)` JsonLogic.
- **Project template.** `dotnet new install DynamicEndpoints.Templates`, then `dotnet new dynamic-endpoints -n MyApi`, creates a
  minimal API with DynamicEndpoints on EF Core SQLite: the admin API with the admin panel at `/admin`, the dynamic OpenAPI document
  with Swagger UI, a typed sample processor and a seeder with demo endpoints. Options: `--DynamicEndpointsVersion`, `--no-admin-ui`
  and `--no-swagger`.
- **Documentation site** (docfx, with an API reference from the XML docs) at
  https://doctorspider42.github.io/dotnet-dynamic-endpoints, built from `docs/` and published by `.github/workflows/docs.yml`.
  The README is now a shorter landing page that links to it.
- **Benchmarks** in `tests/DynamicEndpoints.Benchmarks` (BenchmarkDotNet): dynamic endpoints vs. equivalent minimal APIs, and the
  cost of a routing table swap with 10, 100 and 1000 endpoints. Results are in the README.
- **`GET /info` of the admin API** (`DynamicEndpointsAdminInfo`): whether multi-tenancy is on (with the tenant route prefix and
  header), the tenant of a tenant admin API, whether the store keeps drafts and history, whether a queryable audit log is
  configured, and the available text formats – so clients (the admin panel) show only what works.
- **Snippets with tenants.** With multi-tenancy, example requests and snippets fill the tenant into the tenant route prefix and send
  the header of `FromHeader(…)`. An endpoint of a tenant uses its own; for shared endpoints `?tenant=` picks one (a tenant admin API
  always uses its tenant). `IDynamicEndpointSnippetGenerator` has `CreateExample`/`Generate` overloads with a tenant.
- **Admin panel: the rest of the admin API.** The panel reads `GET /info` and shows only what the server supports.
  - "Try" console: example values and curl / HTTPie / C# snippets come from the server's snippet generator (`GET /{id}/snippets`,
    and `POST /snippets` with the entered values as you type), so the panel and the API never disagree; servers without it fall
    back to the generator in the browser. The editor's **Code** button previews the request of the unsaved definition. Responses
    show `Cache-Control`, `ETag` and `Retry-After`.
  - Editor: **response caching** and **rate limit / quota** sections, and **forms for the built-in processors** (`http-forward`,
    `webhook`, `response`, `sql-query`; JSON for the others, switchable). Server validation errors appear under the field they
    belong to, configuration errors under the field they name; the error list links to the fields.
  - **Export** (all, shown or selected endpoints; JSON or YAML) with a preview and download, and **import** of a file or pasted text
    in create / upsert / sync mode: a dry run lists what would be created, updated (with the changed properties), deleted or
    skipped and the invalid endpoints with their errors; the real import needs a successful dry run and a confirmation.
  - **OpenAPI import wizard**: document, options (processor, route prefix, group, tags, enabled, skip invalid), dry run with the
    operations, what couldn't be mapped and errors, then confirmation.
  - **Multi-tenancy**: a tenant filter on the list (`GET /?tenant=`, `GET /tenants`), a tenant column and a Tenant field in the
    editor; "Try" sends the tenant header / fills the tenant route prefix. Pointed at `MapDynamicEndpointsTenantAdmin`, the panel
    manages that tenant only.
  - **Audit**: an audit view per endpoint and a global audit log with filters (endpoint, tenant, user, time range), showing who,
    when, what and before → after per property. Hidden when the server has no queryable audit log.
  - Selection checkboxes in the list.
- `MapDynamicEndpointsAdminUI` takes patterns with route parameters, filled into the admin API path and links:
  `app.MapDynamicEndpointsAdminUI("/admin/tenants/{tenant}", "/api/admin/tenants/{tenant}/endpoints")` is a tenant's own panel.
  New options `TenantOpenApiUrl` and `TenantSwaggerUrl` (with `{tenant}`) make the header links follow the tenant picked in the panel.
- The sample registers the built-in processors, `sql-query` on its own database, YAML, multi-tenancy (`X-Tenant` header) with a
  tenant admin API and panel (`/admin/tenants/acme/`), per-tenant OpenAPI documents, rate limiting and output caching, and seeds
  a cached and rate-limited `GET /products/{sku}`, an SQL report and a tenant endpoint.
- **CRUD on EF Core entities: the `ef-crud` processor** (`DynamicEndpoints.EntityFrameworkCore`). `AddEntityFrameworkCrud<TContext>(crud =>
  crud.Entity<Product>(e => e.Fields(…).ReadOnly(…).Filterable(…).Sortable(…).TenantColumn(…).Operations(…)))` allowlists entities and
  fields; `AllFields(except: …)` exposes every mapped scalar property, with keys, store-generated values, concurrency tokens and shadow
  properties read-only and navigations left out. Entity names default to `[DynamicEntity]`, then the `DbSet` property name with a
  lower-case first letter (`Products` → `products`); definitions store the name, never a type. The configuration
  (`entity`, `operation` – `list`, `get`, `create`, `update`, `patch`, `delete` – `key`, `pageSize`, `maxPageSize`, `sort`,
  `sortParameter`, `filters`, `requireIfMatch`) is checked against the allowlist and the definition on save and again before every
  request. Only declared body parameters that are writable fields are written; responses project to the exposed fields. Lists
  page with `page`/`pageSize` (`{ items, page, pageSize, total }`), sort and filter on allowlisted fields only (`eq`, `ne`, `lt`, `lte`,
  `gt`, `gte`, `contains`, `startsWith`, `in`) through expression trees with parameters. Concurrency tokens become `ETag`s, checked
  against `If-Match` (`412`, `409`, `428`); create answers `201` with `Location`. `TenantColumn(…)` filters every query by the
  endpoint's – for shared endpoints the request's – tenant and stamps new rows; entities without one are off limits for tenants'
  endpoints unless `SharedAcrossTenants()` or `AllowTenants(…)`. `IDynamicCrudInterceptor<TEntity>` runs before and after writes and
  can reject them. The configuration is checked against the EF model on start.
- **CRUD scaffolding from the EF model.** `IDynamicCrudScaffolder.ScaffoldAsync("products", options)` generates the endpoints an
  entity allows – key in the route, typed parameters with required, max length, decimal ranges and enum values, paging, sort and
  filter parameters – through the transfer like the OpenAPI import: same result shape, existing routes skipped, nothing written when
  one is invalid, disabled unless `Enabled`, origin `{ "kind": "ef-crud" }`. Admin API: `POST /scaffold/crud?entity=…&dryRun=true`
  and `GET /crud/entities`; a tenant's admin API scopes both to its tenant. `DynamicEndpoint.HandledByCrud<TEntity>(operation, …)`
  selects the processor in code. The OpenAPI document shows the exposed fields, the list envelope, `ETag`, `If-Match`, `201`, `204`,
  `404`, `409`, `412` and `428` of `ef-crud` endpoints.
- `DynamicEndpointsAdminInfo.Features` (`GET /info`): features other packages added to the admin API, e.g. `crud`.
- Admin panel: a form for the `ef-crud` configuration (entity picker, operations, fields with their filter operators) and a
  *Scaffold CRUD* wizard with a dry run, shown when `/info` reports `crud`.
- Sample: a `Product` entity with a tenant column exposed through `ef-crud`, an interceptor, and scaffolded `/shop/products`
  endpoints (the local SQLite demo database is recreated when it has no products table).

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
- The release workflow also skips pushes that only touch the benchmarks, the docs workflow or the tool manifest.

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
