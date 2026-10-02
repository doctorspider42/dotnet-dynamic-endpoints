# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project uses [Semantic Versioning](https://semver.org/).

> **How releases work:** every push to `main` publishes a new version (see [Releasing](README.md#-releasing)).
> Add your entries under **[Unreleased]** together with the change. The release workflow moves them under the new version,
> and they become the GitHub Release notes. Pushes without entries are released with auto-generated notes only.

## [Unreleased]

### Added

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
