# Multi-tenancy

Endpoints can belong to a tenant. Each request is resolved to a tenant, and routing serves it the endpoints of its tenant plus the
shared ones (no tenant). Several tenants can use the same route, each with its own parameters, rules and processor configuration.

```csharp
builder.Services.AddDynamicEndpoints()
    .UseMultiTenancy(t => t.FromHeader("X-Tenant-Id"))   // first resolver with a result wins
    .UseEntityFrameworkStore<AppDbContext>();

await manager.CreateAsync(DynamicEndpoint.Get("/orders").HandledBy<OrderProcessor>().Build() with { Tenant = "acme" });
await manager.ForTenant("globex").CreateAsync(DynamicEndpoint.Get("/orders").HandledBy<LegacyOrderProcessor>());   // same route
await manager.CreateAsync(DynamicEndpoint.Get("/health/ready").HandledBy("ready"));                                 // shared
```

`GET /orders` with `X-Tenant-Id: acme` reaches acme's endpoint, with `globex` globex's, and without a tenant it's a `404`.

## Finding the tenant of a request

| Resolver | Tenant from |
|---|---|
| `FromHeader("X-Tenant-Id")` | a request header |
| `FromHost()` | the first label of the host: `acme.example.com`, `acme.localhost` → `acme`. `FromHost(host => …)` maps hosts yourself |
| `FromClaim("tenant_id")` | a claim of the user. Routing runs before the authentication middleware, so the resolver authenticates with the default (or given) scheme itself |
| `FromRoutePrefix("/tenants/{tenant}")` | the path: every dynamic endpoint is routed under the prefix, `/orders` → `/tenants/{tenant}/orders` |
| `From<TResolver>()`, `From(context => …)` | your own `IDynamicEndpointTenantResolver` |

Resolvers run in registration order, at most once per request, and only for routes that have tenant endpoints. Tenant ids are
letters, digits, `-`, `_` and `.` (at most 100 characters) and are compared case-insensitively. `HttpContext.GetDynamicEndpointTenantAsync()`
returns the result in your own middleware, and `request.Tenant` in a processor (for shared endpoints, the tenant of the request).

With a **route prefix**, routes in definitions stay relative to it. Tenant endpoints are only routable under their own tenant's
prefix, shared endpoints under every tenant's. A definition may still bind the prefix parameter with `FromRoute("tenant")`.

## Isolation

`manager.ForTenant(tenant)` is a view of the manager for tenant-facing code. It lists, gets and changes only the endpoints of that
tenant, assigns new and updated definitions to it, and treats the endpoints of other tenants as if they didn't exist (`404`).
`ForTenant(null)` sees the shared endpoints. A scoped view can't be widened again: `ForTenant("a").ForTenant("b")` sees nothing.

```csharp
var changes = manager.ForTenant("acme").BeginChanges(db.GetDynamicEndpointStore());   // change sets too
await changes.UpsertAsync(definition, ct);                                            // → Tenant = "acme"
```

`store.ForTenant(tenant)` does the same for an `IDynamicEndpointStore`: it filters reads and rejects writes of definitions that
belong to another tenant.

**Conflicts** are checked per tenant: two tenants may use the same route, a tenant endpoint and a shared endpoint may not
(the shared one is routable for every tenant).

## Admin API

The global admin API (`MapDynamicEndpointsAdmin`) manages every tenant. Definitions carry `"tenant": "acme"`, `GET /?tenant=acme`
filters the list, and `GET /tenants` lists the tenants that own endpoints.

A tenant's own admin API sees only its endpoints and assigns new ones to it:

```csharp
app.MapDynamicEndpointsTenantAdmin("/api/tenants/{tenant}/endpoints")
   .RequireAuthorization("tenant-admin");   // your policy: does the user belong to the tenant in the route?
```

The tenant comes from the route parameter of the prefix, or from the resolvers when the prefix has none (e.g. `/api/my/endpoints`
with `FromClaim()`). `GET /info` of a tenant admin API names its tenant, and the [admin panel](admin-ui.md#a-panel-per-tenant)
can sit on top of it: `app.MapDynamicEndpointsAdminUI("/admin/tenants/{tenant}", "/api/tenants/{tenant}/endpoints")`.

## Drafts, history, export and import per tenant

Everything on top of the manager follows the tenant's view, so a tenant admin can work with drafts, history and GitOps without
seeing anybody else's endpoints:

| Feature | `manager.ForTenant("acme")` and the tenant's admin API |
|---|---|
| [Drafts](drafts-and-history.md) | lists and gets only the tenant's drafts; saved drafts belong to the tenant; another tenant's endpoint can't be drafted over (its id "already exists") |
| History, diffs, rollback | only for the tenant's endpoints (`404` otherwise); a revision that belonged to another tenant can't be rolled back to |
| Scheduled publishing | `PublishDueAsync()` of a tenant view publishes only that tenant's due drafts; the built-in scheduler publishes every tenant's |
| [Export & import](export-import-gitops.md) | exports, matches and syncs only the tenant's endpoints: `?mode=sync` deletes the tenant's endpoints that aren't in the file, nobody else's; imported endpoints belong to the tenant |
| [OpenAPI import](openapi-import.md) | skeletons belong to the tenant; "route already exists" means the tenant already has it; `mode=upsert`/`sync` update and delete only the tenant's own imported endpoints, never shared ones or another tenant's |
| [Audit log](audit-log.md) | only the tenant's entries |
| Change sets | `ForTenant("acme").BeginChanges(…)` assigns the tenant to definitions and drafts |
| [`sql-query`](built-in-processors.md#with-multi-tenancy) | the tenant's endpoints may only use the connections assigned to it (`AllowTenants`); shared endpoints may use all |
| [Snippets](admin-api.md#snippets) | requests of the tenant: tenant route prefix filled in, tenant header added |
| [`/info`](admin-api.md#info) | `tenant` names the tenant of the admin API |

```bash
dynamic-endpoints diff acme.yaml --url https://api.example.com/api/tenants/acme/endpoints
dynamic-endpoints push acme.yaml --sync --url https://api.example.com/api/tenants/acme/endpoints   # acme only
```

- **Ids are global.** Copying a tenant's export into another tenant keeps the ids, which already exist, so those endpoints are
  reported invalid. Remove the `id`s to copy endpoints between tenants: they are then matched by method and route.
- **Shared endpoints** are invisible to a tenant view. A tenant import or draft on a route a shared endpoint uses fails the
  conflict check.
- The tenant admin API needs the built-in transfer and OpenAPI importer: if you replaced `IDynamicEndpointTransfer` or
  `IDynamicEndpointOpenApiImporter`, their routes fail rather than leak other tenants' endpoints.
- **Admin panel:** the global panel has a tenant filter, a tenant field and a *Try* console that sends the tenant; one
  `MapDynamicEndpointsAdminUI("/admin/tenants/{tenant}", "/api/admin/tenants/{tenant}/endpoints")` call serves a panel per
  tenant on top of the tenant admin API ([admin panel](admin-ui.md#a-panel-per-tenant)).
- **Caching and rate limits:** the output cache keeps the responses of tenant endpoints apart (only shared endpoints whose response
  depends on the tenant need the tenant header in `varyByHeader`); rate limits count per endpoint, so each tenant endpoint has its own budget
  ([caching & rate limits](caching-and-rate-limits.md#with-multi-tenancy)).

## OpenAPI

Endpoints of different tenants can share a path, so there is one document per tenant:

```csharp
app.MapDynamicEndpointsOpenApi("/openapi/{tenant}/dynamic.json");   // shared + the tenant's endpoints
app.MapDynamicEndpointsOpenApi("/openapi/dynamic.json");            // the request's tenant (resolvers), or the shared endpoints only
```

`IDynamicOpenApiDocumentProvider.GetDocument(tenant)` returns the same document in code. With a route prefix, the tenant's
document has the tenant filled in (`/tenants/acme/orders`), and the shared document documents the `{tenant}` path parameter.
Document the tenant header yourself, e.g. `o.OpenApi.AddHeader("X-Tenant-Id", "Tenant.", required: true)`.

## Storage

The tenant is part of the definition, which the EF Core store keeps as JSON, so existing tables need no migration.
`GetAllAsync` of a tenant-scoped store filters in memory.
