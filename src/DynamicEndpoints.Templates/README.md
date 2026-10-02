# DynamicEndpoints.Templates

`dotnet new` project template for [DynamicEndpoints](https://www.nuget.org/packages/DynamicEndpoints): runtime-defined,
persistent HTTP endpoints for ASP.NET Core.

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/doctorspider42/dotnet-dynamic-endpoints/blob/main/LICENSE)

## Install and create a project

```bash
dotnet new install DynamicEndpoints.Templates
dotnet new dynamic-endpoints -n MyApi
cd MyApi && dotnet run
```

## What you get

- An ASP.NET Core minimal API (.NET 10) with `DynamicEndpoints` and `DynamicEndpoints.EntityFrameworkCore` on SQLite.
  Your own `AppDbContext` holds the endpoint definitions. The database is created on start-up.
- `MapDynamicEndpoints()`, the admin REST API at `/api/admin/endpoints` (secure it before going live) and the
  generated OpenAPI document at `/openapi/dynamic.json`.
- Swagger UI at `/swagger` with both documents: the dynamic endpoints and the static (admin) API.
- A typed processor (`DynamicEndpointProcessor<TConfig>` with `[DynamicProcessor]`) and a seeder that creates demo
  endpoints with the fluent builder. `AddFromAssemblyContaining<Program>()` registers both.

## Options

| Option | Default | |
|---|---|---|
| `--DynamicEndpointsVersion` | the template's release line, e.g. `0.3.*` | Version (or floating range) of the DynamicEndpoints packages. |
| `--no-swagger` | `false` | Leave out Swagger UI. |

📖 [Documentation](https://github.com/doctorspider42/dotnet-dynamic-endpoints#readme) ·
📝 [Changelog](https://github.com/doctorspider42/dotnet-dynamic-endpoints/blob/main/CHANGELOG.md)
