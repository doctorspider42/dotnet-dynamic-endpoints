# Admin panel: `MapDynamicEndpointsAdminUI()`

The `DynamicEndpoints.AdminUI` package serves a ready-made panel from embedded files, with no static files middleware and no
dependencies beyond ASP.NET Core:

```bash
dotnet add package DynamicEndpoints.AdminUI
```

```csharp
app.MapDynamicEndpointsAdmin("/api/admin/endpoints").RequireAuthorization("admin");     // the REST API the panel talks to
app.MapDynamicEndpointsAdminUI("/admin", adminApiPath: "/api/admin/endpoints", o =>
{
    o.Title = "Orders API – endpoints";
    o.SwaggerUrl = "/swagger";                  // optional links in the header
    o.OpenApiUrl = "/openapi/dynamic.json";
}).RequireAuthorization("admin");
```

Open `/admin/`. Without arguments the panel is at `/admin` and talks to `/_dynamic-endpoints`, the default prefix of
`MapDynamicEndpointsAdmin()`. `adminApiPath` is relative to the path base, or an absolute URL.

## What's inside

- **Endpoints:** status (`Active`, `Disabled`, `Invalid`, `Pending`, `Draft`), drafts and scheduled publishes at a glance, filter.
- **Editor:** everything a definition has: parameters with constraints and formats, JsonLogic rules, custom validators, processor
  configuration, security, documentation. *Validate*, *Save draft* (with an optional publish time and comment) or *Save & publish*.
- **History:** revisions with kind and comment, view any of them, diffs against the previous or the published revision,
  one-click rollback ([drafts, history & rollback](drafts-and-history.md)).
- **Try console:** a form generated from the definition, with example values built from the parameters' examples, defaults,
  allowed values, formats, lengths, ranges and JSON Schemas (or the definition's request example), plus *Copy as curl / HTTPie /
  C# HttpClient*.

Drafts and history need a store that keeps them (the in-memory and EF Core stores do). With other stores those buttons are hidden.

<img src="../images/editor.jpg" alt="Endpoint editor" width="820">

## Security & hosting

- **The panel holds no data.** Everything goes through the admin REST API, so secure that one in any case.
- Both calls return a `RouteGroupBuilder`, so `.RequireAuthorization()`, `.RequireHost()` and friends work as usual. The panel's
  prefix is reserved, so no dynamic endpoint can take it.
- Works behind a path base (`app.UsePathBase("/tools")`): links and API calls follow it.
- Files are sent with an `ETag`, and the page is never cached, so a deployment never mixes old scripts with a new page.
- The page sets `X-Content-Type-Options: nosniff`, `frame-ancestors 'none'` and `Referrer-Policy: no-referrer`.

## With multi-tenancy

The panel has no tenant picker and doesn't send a tenant with its requests. Pointed at the global admin API it manages every
tenant's endpoints. To give tenants a panel of their own, point it at a [tenant admin API](multi-tenancy.md#admin-api) whose
prefix has no route parameter, so the resolvers find the tenant:

```csharp
builder.Services.AddDynamicEndpoints().UseMultiTenancy(t => t.FromClaim("tenant_id"));

app.MapDynamicEndpointsTenantAdmin("/api/my/endpoints").RequireAuthorization("tenant-admin");
app.MapDynamicEndpointsAdminUI("/my/admin", adminApiPath: "/api/my/endpoints").RequireAuthorization("tenant-admin");
```

The *Try* console calls the route as it is defined: it sends no tenant header and doesn't add a tenant route prefix. It reaches
tenant endpoints when the tenant comes from the host or from the signed-in user (`FromClaim()` with cookie authentication), and
only shared endpoints with `FromHeader()` or `FromRoutePrefix()`.
