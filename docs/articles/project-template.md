# Project template

Start a new application with everything wired up:

```bash
dotnet new install DynamicEndpoints.Templates
dotnet new dynamic-endpoints -n MyApi
cd MyApi && dotnet run
```

You get an ASP.NET Core minimal API (.NET 10) with:

- `DynamicEndpoints` + `DynamicEndpoints.EntityFrameworkCore` on SQLite, in your own `AppDbContext`,
- the admin REST API at `/api/admin/endpoints` (secure it before going live) and the generated `/openapi/dynamic.json`,
- Swagger UI with both the dynamic and the static API (`--no-swagger` leaves it out),
- a typed sample processor and a seeder with demo endpoints, registered by `AddFromAssemblyContaining<Program>()`.

`--DynamicEndpointsVersion` picks the package version (default: the template's release line, e.g. `0.3.*`).
