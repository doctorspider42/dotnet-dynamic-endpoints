# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project uses [Semantic Versioning](https://semver.org/).

> **How releases work:** every push to `main` publishes a new version (see [Releasing](README.md#-releasing)).
> Add your entries under **[Unreleased]** together with the change. The release workflow moves them under the new version,
> and they become the GitHub Release notes. Pushes without entries are released with auto-generated notes only.

## [Unreleased]

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

[Unreleased]: https://github.com/doctorspider42/dotnet-dynamic-endpoints/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/doctorspider42/dotnet-dynamic-endpoints/compare/v0.1.2...v0.2.0
[0.1.2]: https://github.com/doctorspider42/dotnet-dynamic-endpoints/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/doctorspider42/dotnet-dynamic-endpoints/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/doctorspider42/dotnet-dynamic-endpoints/releases/tag/v0.1.0
