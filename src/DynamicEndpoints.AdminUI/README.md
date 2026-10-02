# DynamicEndpoints.AdminUI

A ready-made admin panel for [DynamicEndpoints](https://www.nuget.org/packages/DynamicEndpoints): list, editor, drafts and
scheduled publishing, history with diffs and rollback, and a "Try" console. Served straight from the assembly, so there are
no static files to copy and no dependencies beyond ASP.NET Core.

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/doctorspider42/dotnet-dynamic-endpoints/blob/main/LICENSE)

```csharp
app.MapDynamicEndpointsAdmin("/api/admin/endpoints").RequireAuthorization("admin");     // the REST API the panel talks to
app.MapDynamicEndpointsAdminUI("/admin", adminApiPath: "/api/admin/endpoints", o =>
{
    o.Title = "Orders API – endpoints";
    o.SwaggerUrl = "/swagger";                  // optional links in the header
    o.OpenApiUrl = "/openapi/dynamic.json";
}).RequireAuthorization("admin");
```

Open `/admin/`. The panel holds no data itself: everything goes through the admin REST API, so secure that one in any case.
Both calls return a `RouteGroupBuilder`, so `.RequireAuthorization()`, `.RequireHost()` and friends work as usual. The panel's
prefix is reserved, so no dynamic endpoint can take it.

## What's inside

- **Endpoints:** status (`Active`, `Disabled`, `Invalid`, `Pending`, `Draft`), drafts and scheduled publishes at a glance, filter, selection.
- **Editor:** parameters with constraints and formats, JsonLogic rules, custom validators, processor configuration (forms for
  `http-forward`, `webhook`, `response`, `sql-query` and `ef-crud` – with the exposed entities and their fields – or JSON), security, caching and rate limits, documentation. *Validate*,
  *Save draft* (optionally with a publish time and a comment) or *Save & publish*; *Code* shows the request as curl, HTTPie or C#.
- **History:** every revision with its kind and comment, view any of them, diff with the previous or the published one, roll back.
- **Try:** a form generated from the definition, filled from the server's snippet generator, with *Copy as curl / HTTPie /
  C# HttpClient*.
- **Export & import:** all, shown or selected endpoints as JSON or YAML; import with `create`, `upsert` or `sync`, always after
  a dry run. Plus an **OpenAPI import** wizard.
- **Scaffold CRUD:** pick an entity exposed with `AddEntityFrameworkCrud`, the operations and a route prefix, check the dry run,
  create the endpoints (DynamicEndpoints.EntityFrameworkCore).
- **Audit log:** per endpoint and for everything, with filters.
- **Tenants:** tenant filter and column, a tenant field in the editor, and a *Try* console that sends the tenant.

The panel asks the admin API's `GET /info` what the server supports and shows only that: drafts and history need a store that
keeps them, the audit log a queryable sink, YAML `AddYamlFormat()`, tenants `UseMultiTenancy()`,
the `ef-crud` form and *Scaffold CRUD* `AddEntityFrameworkCrud()` (with at least one entity the admin API's tenant may use).

## A panel per tenant

Route parameters of the pattern are filled into the admin API path and the links:

```csharp
app.MapDynamicEndpointsTenantAdmin("/api/admin/tenants/{tenant}/endpoints").RequireAuthorization("tenant-admin");
app.MapDynamicEndpointsAdminUI("/admin/tenants/{tenant}", "/api/admin/tenants/{tenant}/endpoints", o =>
    o.OpenApiUrl = "/openapi/{tenant}/dynamic.json").RequireAuthorization("tenant-admin");
```

Secure both with a policy that checks the user belongs to the tenant. On the global panel, `TenantOpenApiUrl` and
`TenantSwaggerUrl` (with a `{tenant}` placeholder) follow the tenant picked in the filter.

## Notes

- Works behind a path base (`app.UsePathBase("/tools")`): links and API calls follow it.
- Files are sent with an `ETag`, and the page is never cached, so a deployment never mixes old scripts with a new page.
- The page sets `X-Content-Type-Options: nosniff`, `frame-ancestors 'none'` and `Referrer-Policy: no-referrer`.

📖 [Documentation](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/admin-ui.html) ·
📝 [Changelog](https://github.com/doctorspider42/dotnet-dynamic-endpoints/blob/main/CHANGELOG.md)
