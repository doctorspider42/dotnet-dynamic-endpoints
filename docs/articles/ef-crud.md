# CRUD on EF Core entities: `ef-crud`

The `ef-crud` processor of `DynamicEndpoints.EntityFrameworkCore` turns entities of your `DbContext` into list, get, create,
update, patch and delete endpoints. You decide which entities and which of their fields admins may use; admins build the
endpoints – by hand, or scaffolded from the EF model in one click. No new dependencies: it uses EF Core, which the package
already references.

```csharp
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));
builder.Services.AddDynamicEndpoints()
    .AddEntityFrameworkCrud<AppDbContext>(crud => crud
        .Entity<Product>(e => e                       // "products" – the DbSet property name
            .Fields(p => p.Id, p => p.Sku, p => p.Name, p => p.Price)
            .ReadOnly(p => p.CreatedAt)
            .Filterable(p => p.Name, p => p.Price)
            .Sortable(p => p.Name)
            .Operations(CrudOperations.All & ~CrudOperations.Delete))
        .Entity<Order>("orders", e => e.AllFields(except: o => o.InternalNote)));
```

```json
{
  "method": "GET",
  "route": "/products",
  "processor": "ef-crud",
  "processorConfig": { "entity": "products", "operation": "list", "pageSize": 50, "maxPageSize": 200, "sort": "name", "sortParameter": "sort" },
  "parameters": [
    { "name": "page", "source": "Query", "type": "Integer", "minimum": 1 },
    { "name": "pageSize", "source": "Query", "type": "Integer", "minimum": 1, "maximum": 200 },
    { "name": "sort", "source": "Query" }
  ]
}
```

`GET /products?page=2&sort=-price` answers `{ "items": [ … ], "page": 2, "pageSize": 50, "total": 120 }`.

## The allowlist

Nothing of the context is reachable unless the application exposes it.

- **Entities** are exposed one by one with `.Entity<T>(…)`. The admin's configuration stores the entity's **name**, never its CLR
  type: renaming or moving the class doesn't break definitions, and no type names leak to the admin API, the panel or exports.
- **The name** is, in this order: the string passed to `.Entity<T>("orders", …)`; the `[DynamicEntity("…")]` attribute of the
  class; the name of the context's `DbSet<T>` property with the first letter lower-cased (`Products` → `products`,
  `OrderLines` → `orderLines`); the type name the same way (`Product` → `product`, no pluralization). The attribute beats the
  property because it is an explicit choice. Names are letters, digits, `-` and `_`, compared case-insensitively, and must be
  unique across all `AddEntityFrameworkCrud` calls.
- **Fields** are exposed with `.Fields(…)` (readable and writable), `.ReadOnly(…)` (readable only), or `.AllFields()` /
  `.AllFields(except: …)`: every mapped scalar property – shadow properties included – except the ones listed. Field names in
  requests and responses are camelCase (`CreatedAt` → `createdAt`).
- **Automatically read-only:** store-generated values (identity keys, `ValueGeneratedOnAdd`, computed columns), concurrency tokens,
  shadow properties and the tenant column. Keys are never changed by update or patch; a key that isn't generated (a country code,
  a SKU used as the key) can be set on create.
- **Never exposed:** navigations (references and collections), owned types and properties of types without a JSON mapping
  (`byte[]`, spatial types, …) – `AllFields` leaves them out, `Fields` refuses them. Foreign key properties are plain scalars and
  *are* exposed by `AllFields`.
- **Filterable** and **sortable** fields must be exposed. **Operations** default to all; entities without a single-property key
  (keyless entities, composite keys) support `list` only.

> [!WARNING]
> `AllFields` exposes properties added to the entity **later** automatically – a new `PasswordHash` or `InternalNote` column is
> public the moment it is mapped. Prefer `.Fields(…)` for entities that may grow sensitive columns, or keep the `except` list
> current.

The configuration is checked against the EF model when the application starts (a hosted service builds it), with all problems
in one exception: unknown or unmapped properties, navigations listed as fields, filterable fields that aren't exposed, a tenant
column that isn't a string, operations that need a key the entity doesn't have, create without a parameterless constructor.

### No mass assignment

The processor never binds the request body to the entity. It writes exactly the **body parameters the definition declares**,
and only when they are writable fields:

- Declaring a body parameter that isn't an exposed field (`internalNote`), or is read-only (`id`, `createdAt`, the tenant column),
  is a validation error **when the definition is saved**.
- Fields a client sends without a declaration are **ignored** – like for every dynamic endpoint, the binder only hands declared
  parameters to the processor. `{ "name": "Anvil", "tenantId": "other", "id": 999 }` writes the name and nothing else.
- Responses contain the exposed fields only – the query projects to them, so unlisted columns aren't even read.

If the allowlist is tightened after a definition was saved (a field or operation removed), the endpoint answers `500` instead of
running (`The configuration of this endpoint is not allowed.`; the reason is logged), and a definition saved again gets the new
validation errors.

## Operations

| `operation` | Method | Route | Answer |
|---|---|---|---|
| `list` | GET | `/products` | `200 { items, page, pageSize, total }` |
| `get` | GET | `/products/{id}` | `200` with `ETag`; `404` |
| `create` | POST | `/products` | `201` with `Location` and `ETag` |
| `update` | PUT | `/products/{id}` | `200` with the new `ETag`; `404`, `409`, `412`, `428` |
| `patch` | PATCH | `/products/{id}` | same as update |
| `delete` | DELETE | `/products/{id}` | `204`; `404`, `409`, `412`, `428` |

The method must match the operation (a `GET` endpoint can't delete). Request bodies are JSON (`Body` parameters).

- **Key:** from the route parameter named like the key field (`{id}`), or the one named in `key`. It must be declared with
  source `Route`. Only single-property keys are supported; composite keys are list-only.
- **Create** fills a new entity from the declared body parameters, stamps the tenant, gives a `Guid` concurrency token its first
  value and answers `201` with the exposed fields. `Location` is the request path plus the key (`POST /products` →
  `/products/42`).
- **Update (PUT)** replaces the declared writable fields: one that isn't sent becomes `null`, or the CLR default for value types
  (`0`, `false`). Non-nullable strings are required. Fields the definition doesn't declare are left alone.
- **Patch** changes only the fields sent. JSON `null` counts as "not sent" (the binder treats them alike) – use update to clear a field.
- **Delete** removes the entity.

## Lists: paging, sorting, filters

- **Paging** is `page` / `pageSize` (1-based), read from the query parameters of those names when the definition declares them.
  `pageSize` defaults to `pageSize` of the configuration (50), and a request above `maxPageSize` (200) – or a `page` below 1 –
  is a `400`. `total` is the number of matching rows (one `COUNT` query); turn it off with `"includeTotal": false`.
- **Sorting:** `sort` is the default order, `sortParameter` the query parameter with the client's order. Both use
  `name,-price` (at most five fields, `-` for descending) and accept **sortable fields only** – a configuration with another field
  is rejected on save, a request with one gets a `400` that lists the sortable fields. The key is always appended, so pages are stable.
- **Filters** compare a filterable field with a request parameter (query, header or route) or a fixed `value`; all of them must
  match, and a filter whose parameter is absent is skipped.

```json
"filters": [
  { "field": "price", "operator": "gte", "parameter": "minPrice" },
  { "field": "name", "operator": "startsWith", "parameter": "q" },
  { "field": "status", "operator": "in", "value": ["Active", "Draft"] }
]
```

| Field type | Operators |
|---|---|
| string | `eq`, `ne`, `contains`, `startsWith`, `in` |
| numbers, dates, times | `eq`, `ne`, `lt`, `lte`, `gt`, `gte`, `in` |
| boolean | `eq`, `ne` |
| GUID, enum | `eq`, `ne`, `in` |

`in` takes an array (at most 100 values; an array parameter, e.g. `?skus=A&skus=B`). String comparisons follow the database
collation. Queries are **expression trees** built from the allowlist – values are always SQL parameters, there is no dynamic LINQ
text and no raw SQL anywhere.

> [!NOTE]
> SQLite can't compare or sort `decimal` columns. Map them with `.HasConversion<double>()` there (the [ef-crud sample](samples.md) does), or don't make
> them filterable or sortable.

## Values

| CLR type | JSON | Parameter type |
|---|---|---|
| `string` | string | `String` (`maxLength` from `HasMaxLength`) |
| `bool` | boolean | `Boolean` |
| `int`, `long`, `short`, `byte`, … | number | `Integer` (small types get their range) |
| `decimal`, `double`, `float` | number | `Number` (`decimal` with precision and scale gets its range: (10, 2) → ±99999999.99) |
| `Guid` | string | `Guid` |
| `DateTime`, `DateTimeOffset` | ISO 8601 string | `DateTime` |
| `DateOnly` | `2026-01-31` | `Date` |
| `TimeOnly` | `09:30:00` | `String` with format `Time` |
| enums | the member name (`"Active"`) | `String` with the names as allowed values |

## Concurrency and ETags

When the entity has concurrency tokens (`IsConcurrencyToken()`, `[ConcurrencyCheck]`, `IsRowVersion()`), get, create, update and
patch answer with a strong `ETag` made from their values. Update, patch and delete compare `If-Match` with the current ETag:

- a different ETag → `412 Precondition Failed`, nothing is changed;
- `If-Match: *` matches any existing entity;
- no `If-Match` → the change goes through, unless the configuration has `"requireIfMatch": true` (`428 Precondition Required`);
- a change by another request between reading and saving (EF's `DbUpdateConcurrencyException`) → `412` when the request sent
  `If-Match`, else `409 Conflict`.

Tokens the database generates (`rowversion`) are left to it; client-side tokens get a new value on every change (`Guid`: a new
one, `int`/`long`: +1, `DateTime`/`DateTimeOffset`: now). Entities without a token have no ETag; only `If-Match: *` passes then.
All errors are built by the [error response factory](error-responses.md), like the validation errors of every endpoint.

## Multi-tenancy

**Optional.** Without `UseMultiTenancy()` and without `TenantColumn` nothing here applies: the rows are simply shared and
requests need no tenant – the quickest way to expose an entity. A `TenantColumn` without `UseMultiTenancy()` fails the start
(no request would ever have a tenant). With multi-tenancy on, leave `TenantColumn` out of entities whose rows aren't owned by a
tenant.

```csharp
.Entity<Product>(e => e.Fields(…).TenantColumn(p => p.TenantId))       // rows belong to tenants
.Entity<Country>(e => e.AllFields().SharedAcrossTenants())             // reference data, the same for everyone
.Entity<Report>(e => e.AllFields().AllowTenants("acme"))               // no tenant column, but acme may use it
```

- **Entities with a tenant column** (a string property, or a shadow property by name): every query of every operation is filtered
  by the tenant, and create stamps it. The column is never writable from a request, and interceptors can't move a row to another
  tenant either (it is set again before saving).
- **Which tenant:** the endpoint's own tenant for a tenant's endpoints; the tenant the request was resolved to for shared
  endpoints (`DynamicRequest.Tenant`). A tenant's endpoint is only routed for requests of its tenant, so it never sees other
  tenants' rows; a shared endpoint serves every tenant with that tenant's rows. A request **without** a tenant gets `404` (`The
  request has no tenant…`, naming the header when the tenant comes from one) – it never sees all rows. The OpenAPI document lists
  that header as required on such shared endpoints, so Swagger UI can send it.
- **Entities without a tenant column** are for shared endpoints only – a tenant's admin can't read or change the application's
  data – unless `SharedAcrossTenants()` (every tenant, same rows) or `AllowTenants(…)` (these tenants) allows it. On an entity with
  a tenant column, `AllowTenants` narrows the tenants that may use it.
- Checked **when a definition is saved** (with the definition's tenant – the error lists only the entities that tenant may use) and
  **before every request**: an endpoint whose entity was taken away from its tenant answers `500`.
- A tenant's admin API (`MapDynamicEndpointsTenantAdmin`) lists and scaffolds only the entities its tenant may use.

## Interceptors

Application code around writes – defaults, server-side values, business checks, side effects:

```csharp
public sealed class ProductRules : IDynamicCrudInterceptor<Product>
{
    public ValueTask BeforeCreateAsync(DynamicCrudContext<Product> context)
    {
        context.Entity.CreatedAt = DateTimeOffset.UtcNow;    // any property, exposed or not
        if (context.Entity.Price < 0)
        {
            context.Reject("price", "The price can't be negative.");   // 400, nothing is saved
        }

        return ValueTask.CompletedTask;
    }
}

builder.Services.AddScoped<IDynamicCrudInterceptor<Product>, ProductRules>();
```

`BeforeCreateAsync`, `BeforeUpdateAsync` (update and patch), `BeforeDeleteAsync` run before `SaveChanges` and may change the entity
or reject the request; `AfterCreateAsync`, `AfterUpdateAsync`, `AfterDeleteAsync` run after it. Every registered interceptor runs,
in registration order, in the request scope; the context has the entity, the operation, the request, the `DbContext` and the
tenant. Interceptors are about **data**. Changes of endpoint *definitions* are still reported by
[change events](change-events.md) and the [audit log](audit-log.md) – a CRUD request doesn't produce either.

## Scaffolding from the model

`IDynamicCrudScaffolder` generates the endpoints of an entity from its EF metadata: one per allowed operation, with the key in the
route, typed parameters, required fields, max lengths, decimal ranges, enum values, paging and sort parameters, and a filter
parameter per filterable field (`price`, plus `minPrice` / `maxPrice` for numbers and dates).

```csharp
var result = await scaffolder.ScaffoldAsync("products", new DynamicCrudScaffoldOptions
{
    RoutePrefix = "/shop/products",               // default: "/" + the entity name
    Group = "Shop",                               // default: the entity name
    Operations = CrudOperations.Read | CrudOperations.Create,
    Enabled = true,                               // default false: review first
    DryRun = false,
});
```

It works like the [OpenAPI import](openapi-import.md) and returns the same shape: `operations` with `action` (`Create`, `Skip`,
`Invalid`), the `definition` and its `errors`, plus `created`, `skipped`, `invalid`. Endpoints whose method and route exist are
skipped, nothing is written when one is invalid, and new endpoints are **disabled** unless `Enabled` is set. Scaffolded
definitions record their origin: `{ "kind": "ef-crud", "document": "products", "operation": "list" }`. `Scaffold(…)` returns the
definitions without validating or saving them.

In the admin API:

```http
POST /_dynamic-endpoints/scaffold/crud?entity=products&routePrefix=/shop/products&operation=list&operation=get&dryRun=true
GET  /_dynamic-endpoints/crud/entities
```

`operation` may repeat or hold a comma-separated list (default: all the entity allows); `group`, `enabled=true` and `dryRun=true`
as above. `422` when an endpoint is invalid, `404` for an entity that isn't exposed (to the tenant). `GET /crud/entities` lists
the exposed entities with their operations, key, tenant column and fields (JSON Schema, read-only, creatable, required,
filterable with operators, sortable). `GET /info` reports `"features": ["crud"]` when at least one entity is available, so clients
like the [admin panel](admin-ui.md) show the feature only then. A tenant's admin API scaffolds through its tenant's view of the
transfer: the endpoints belong to the tenant, and only its own routes count as existing.

## In code

```csharp
DynamicEndpoint.Get("/products").HandledByCrud<Product>(CrudOperation.List, c => c.PageSize = 20)
    .FromQuery("page", p => p.Integer().Min(1))
    .FromQuery("pageSize", p => p.Integer().Range(1, 200));

DynamicEndpoint.Patch("/products/{id}").HandledByCrud<Product>(CrudOperation.Patch, c => c.RequireIfMatch = true)
    .FromRoute("id", p => p.Integer())
    .FromBody("name", p => p.String().MaxLength(100));
```

`HandledByCrud<T>` resolves the entity name like the registration does (an explicit name passed to `.Entity<T>("orders", …)`
included), so seeders need no magic strings. Overloads take the entity name, and the processor name when it isn't `ef-crud`.

## OpenAPI

The generated document describes `ef-crud` operations from the live model: the response schema of the exposed fields (read-only
ones marked `readOnly`) and the list envelope, `201` with `Location` for create, `204` for delete, `ETag` response headers, the
`If-Match` header (required with `requireIfMatch`), `404`, `409`, `412` and `428` responses, and `x-dynamic-endpoint.crud`
with the entity and operation. A `responseSchema` in the definition wins over the generated one.

## More than one context

Call `AddEntityFrameworkCrud` once per context, each with its own processor name:
`.AddEntityFrameworkCrud<ShopDbContext>(…).AddEntityFrameworkCrud<HrDbContext>(…, name: "hr-crud")`. Entity names are unique
across all of them.

See also: [Security](security.md), [Multi-tenancy](multi-tenancy.md), [EF Core & migrations](ef-core.md).
