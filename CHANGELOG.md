# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project uses [Semantic Versioning](https://semver.org/).

> **How releases work:** every push to `main` publishes a new version (see [Releasing](README.md#-releasing)).
> Add your entries under **[Unreleased]** together with the change. The release workflow moves them under the new version,
> and they become the GitHub Release notes. Pushes without entries are released with auto-generated notes only.

## [Unreleased]

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

[Unreleased]: https://github.com/doctorspider42/dotnet-dynamic-endpoints/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/doctorspider42/dotnet-dynamic-endpoints/releases/tag/v0.1.0
