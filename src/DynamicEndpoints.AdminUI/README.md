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

- **Endpoints:** status (`Active`, `Disabled`, `Invalid`, `Pending`, `Draft`), drafts and scheduled publishes at a glance, filter.
- **Editor:** parameters with constraints and formats, JsonLogic rules, custom validators, processor configuration, security,
  documentation. *Validate*, *Save draft* (optionally with a publish time and a comment) or *Save & publish*.
- **History:** every revision with its kind and comment, view any of them, diff with the previous or the published one, roll back.
- **Try:** a form generated from the definition, with example values built from parameter examples, defaults, allowed
  values, formats, lengths, ranges and custom JSON Schemas (or the definition's request example). *Copy as curl / HTTPie /
  C# HttpClient* turns the request into code.

Drafts and history need a store that keeps them (the in-memory and EF Core stores do). With other stores those buttons are hidden.

## Notes

- Works behind a path base (`app.UsePathBase("/tools")`): links and API calls follow it.
- Files are sent with an `ETag`, and the page is never cached, so a deployment never mixes old scripts with a new page.
- The page sets `X-Content-Type-Options: nosniff`, `frame-ancestors 'none'` and `Referrer-Policy: no-referrer`.

📖 [Documentation](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/admin-ui.html) ·
📝 [Changelog](https://github.com/doctorspider42/dotnet-dynamic-endpoints/blob/main/CHANGELOG.md)
