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

- **Endpoints:** status (`Active`, `Disabled`, `Invalid`, `Pending`, `Draft`), drafts and scheduled publishes at a glance, filter,
  and a selection for exports.
- **Editor:** everything a definition has: parameters with constraints and formats, JsonLogic rules, custom validators, processor
  configuration, security, caching and rate limits, documentation. *Validate*, *Save draft* (with an optional publish time and
  comment) or *Save & publish*. *Code* shows the example request of the unsaved definition as curl, HTTPie or C#.
- **Processor forms:** the [built-in processors](built-in-processors.md) `http-forward`, `webhook`, `response` and `sql-query`,
  and [`ef-crud`](ef-crud.md), get a form for their configuration, with a *Form / JSON* toggle. Other processors (and built-in ones registered under a name
  of your own) are configured as JSON, with their configuration example one click away.
- **Caching & rate limits:** `Cache-Control` (max age, visibility, no-store), ETag, output cache and vary settings, and a rate
  limit with algorithm, partition and an optional quota ([caching & rate limits](caching-and-rate-limits.md)).
- **History:** revisions with kind and comment, view any of them, diffs against the previous or the published revision,
  one-click rollback ([drafts, history & rollback](drafts-and-history.md)).
- **Try console:** a form generated from the definition, filled with the example values of the server's snippet generator (the
  same as [`GET /{id}/snippets`](admin-api.md#snippets)), and *Copy as curl / HTTPie / C# HttpClient*. The code follows what you
  type.
- **Export:** all endpoints, the ones shown in the list or the selected ones, as JSON or YAML, with a preview
  ([export, import & GitOps](export-import-gitops.md)).
- **Import:** upload or paste an export, pick `create`, `upsert` or `sync`. A dry run always comes first and shows what would
  be created, updated, deleted or is invalid (`422`: nothing is written); only then can the import be confirmed.
- **OpenAPI import:** a wizard for [Import from OpenAPI](openapi-import.md): document, mode (`create`, `upsert`, `sync`),
  options (processor, route prefix, group, tags, document id, mock, enabled, skip invalid) and – once a dry run read the
  document's tags – a tag → processor table. The dry run lists per operation what would happen (create, update with the changed
  properties, unchanged, delete, skip with the reason, invalid), the processor and where it came from, the definition and the
  unmapped details; *Import* asks for confirmation, naming what will be created, updated and deleted.
- **ef-crud form:** an entity picker and the operations it allows (from `GET /crud/entities`), paging, sort and filter
  settings, and a table of the entity's fields – type, writable, filter operators, sortable – so parameters can be named after them.
- **Scaffold CRUD:** a wizard for [scaffolding from the EF model](ef-crud.md#scaffolding-from-the-model): entity, operations,
  route prefix, group, enabled. The dry run lists the endpoints that would be created or skipped (the route exists) with their
  definitions and errors; *Create* asks for confirmation.
- **Audit log:** per endpoint, and the whole log with filters for endpoint, tenant, user and time ([audit log](audit-log.md)).

<img src="../images/editor.jpg" alt="Endpoint editor" width="820">

## Only what the server supports

On start the panel asks [`GET /info`](admin-api.md#info) what the admin API supports. Tenancy shows up only with
`UseMultiTenancy()`, the audit log only with a queryable sink, YAML only with `AddYamlFormat()`, the `ef-crud` form and *Scaffold CRUD* only when
`features` contains `crud` (`AddEntityFrameworkCrud()` with an entity the admin API's tenant may use). Drafts and history appear when
the store keeps them (the in-memory and EF Core stores do); with other stores those buttons are hidden.

Against an older server without `/info` the panel still works: the audit log shows when `/audit` answers, tenancy once an
endpoint has a tenant, and the *Try* console and the code snippets are generated in the browser when the server's snippet
endpoints don't answer.

## Multi-tenancy

With `UseMultiTenancy()` the panel on the global admin API manages every tenant:

- The list has a **tenant filter** (all tenants, shared only, or one tenant) and a tenant column.
- The editor has a **Tenant** field (empty: shared by all tenants). New endpoints start with the tenant of the filter.
- The **Try** console calls the endpoint as its tenant: the tenant header of a `FromHeader()` resolver and/or the tenant route
  prefix are added. For a shared endpoint it asks which tenant to call it as.
- Header links can follow the tenant picked in the filter:

```csharp
app.MapDynamicEndpointsOpenApi("/openapi/dynamic.json");
app.MapDynamicEndpointsOpenApi("/openapi/{tenant}/dynamic.json");
app.MapDynamicEndpointsAdminUI("/admin", "/api/admin/endpoints", o =>
{
    o.OpenApiUrl = "/openapi/dynamic.json";                 // all tenants / the shared endpoints
    o.TenantOpenApiUrl = "/openapi/{tenant}/dynamic.json";  // the tenant picked in the panel
    o.TenantSwaggerUrl = "/swagger/{tenant}";               // likewise, if you host one per tenant
});
```

### A panel per tenant

The panel's pattern may have route parameters. They are filled into `adminApiPath`, the links and the panel's own asset URLs, so
one call serves every tenant's panel on top of a [tenant admin API](multi-tenancy.md#admin-api):

```csharp
app.MapDynamicEndpointsTenantAdmin("/api/admin/tenants/{tenant}/endpoints").RequireAuthorization("tenant-admin");
app.MapDynamicEndpointsAdminUI("/admin/tenants/{tenant}", "/api/admin/tenants/{tenant}/endpoints", o =>
{
    o.OpenApiUrl = "/openapi/{tenant}/dynamic.json";
}).RequireAuthorization("tenant-admin");
```

`/admin/tenants/acme/` then manages only acme's endpoints, drafts, history, exports, imports and audit entries, and new endpoints
belong to acme. Secure **both** calls with a policy that checks the user belongs to the tenant in the route. A pattern with
parameters reserves its literal start (`/admin/tenants`).

A tenant admin API whose prefix has no route parameter works too (`/api/my/endpoints` with `FromClaim()`): the resolvers find
the tenant, and `/info` tells the panel which one it is.

## Security & hosting

- **The panel holds no data.** Everything goes through the admin REST API, so secure that one in any case.
- Both calls return a `RouteGroupBuilder`, so `.RequireAuthorization()`, `.RequireHost()` and friends work as usual. The panel's
  prefix is reserved, so no dynamic endpoint can take it.
- Works behind a path base (`app.UsePathBase("/tools")`): links and API calls follow it.
- Files are sent with an `ETag`, and the page is never cached, so a deployment never mixes old scripts with a new page.
- The page sets `X-Content-Type-Options: nosniff`, `frame-ancestors 'none'` and `Referrer-Policy: no-referrer`.
