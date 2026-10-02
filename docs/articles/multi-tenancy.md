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
with `FromClaim()`).

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
