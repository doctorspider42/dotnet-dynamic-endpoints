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
- the [admin panel](admin-ui.md) at `/admin/` next to it (`DynamicEndpoints.AdminUI`; `/` redirects there),
- Swagger UI with both the dynamic and the static API,
- a typed sample processor and a seeder with demo endpoints, registered by `AddFromAssemblyContaining<Program>()`.

| Option | Default | |
|---|---|---|
| `--DynamicEndpointsVersion` | the template's release line, e.g. `0.4.*` | version (or floating range) of the DynamicEndpoints packages |
| `--no-admin-ui` | `false` | leave out the admin panel |
| `--no-swagger` | `false` | leave out Swagger UI |

Secure the panel like the admin API: both hold the keys to your endpoints.
