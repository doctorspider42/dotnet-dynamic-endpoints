# 06 – Multi-tenancy

Endpoints can belong to a tenant. Here the tenant comes from the `X-Tenant` header; routing serves each request the endpoints of
its tenant plus the shared ones, and tenants can use the same route with their own parameters and configuration. On top: a
tenant's own admin API and panel, an OpenAPI document per tenant, ef-crud endpoints that keep each tenant's rows apart, and
SQL connections assigned to tenants. Tenants here: `acme` and `globex`.

| Endpoint | Tenant | Shows |
|---|---|---|
| `GET /status` | shared | answers every tenant, and requests without one |
| `GET /welcome?name=` | `acme` **and** `globex` | the same route, two definitions, two configurations; without `X-Tenant` it's a `404` |
| `GET /promo` | `acme` | only acme's requests are routed to it |
| `GET /reports/stock` | `acme` | `sql-query` on the connection `acme-reports`, which only acme may use |
| `GET` · `POST /products`, `GET` · `PUT` · `PATCH /products/{id}` | shared, ef-crud with a tenant column | one endpoint for all tenants, each sees and writes its own rows; no tenant → `404`, never all rows |
| `GET /countries`, `GET /countries/{code}` | shared, ef-crud `SharedAcrossTenants()` | reference data, the same for everybody |

| File | What to look at |
|---|---|
| [`Program.cs`](Program.cs) | `UseMultiTenancy(t => t.FromHeader("X-Tenant"))`, `TenantColumn` / `SharedAcrossTenants`, `AllowTenants` of sql-query, `MapDynamicEndpointsTenantAdmin`, the panel per tenant, OpenAPI per tenant |
| [`MultiTenancySeeder.cs`](MultiTenancySeeder.cs) | `manager.ForTenant("acme")` – the tenant view of the manager – and `definition with { Tenant = "globex" }` |
| [`AppDbContext.cs`](AppDbContext.cs) | `Product.TenantId`, the tenant column |
| [`multi-tenancy.http`](multi-tenancy.http) | the requests below, with `X-Tenant` |

## Run it

```bash
dotnet run --project samples/06-MultiTenancy
```

Definitions and data in `multi-tenancy.db` (in the sample's folder, the working directory of `dotnet run`).

- **All tenants:** <http://localhost:5106/admin/> – the tenant filter at the top, the tenant column, a *Tenant* field in the
  editor (empty = shared), and a *Try* console that sends `X-Tenant` (for a shared endpoint it asks which tenant to call it as).
- **acme only:** <http://localhost:5106/admin/tenants/acme/> – on the tenant admin API `/api/admin/tenants/acme/endpoints`. It
  lists, creates, drafts, exports and imports only acme's endpoints; what you create there belongs to acme. Try
  `/admin/tenants/globex/` too.
- **Swagger UI:** <http://localhost:5106/swagger> – one document per tenant (*Tenant acme*, *Tenant globex*), the shared one and
  the admin API.

## Click around

- In acme's panel, create `GET /promo` – rejected, acme has it already. In globex's panel the same works: another tenant's route
  doesn't conflict. A shared endpoint on `/promo` would conflict with both.
- In acme's panel, create a `sql-query` endpoint without a connection: rejected, the error lists only `acme-reports`. In globex's
  panel `acme-reports` is rejected too.
- In the global panel, switch the filter to *Shared only*, open the scaffolded *products* list and use *Try* as `acme`, then as `globex`.

## Call it

```bash
curl -H "X-Tenant: acme" http://localhost:5106/welcome                # Welcome to Acme, guest!
curl -H "X-Tenant: globex" "http://localhost:5106/welcome?name=Ann"   # Globex greets you, Ann.
curl -i http://localhost:5106/welcome                                 # 404 – no tenant
curl -i -H "X-Tenant: globex" http://localhost:5106/promo             # 404 – acme's endpoint

curl -H "X-Tenant: acme" "http://localhost:5106/products?sort=-price"   # acme's three products
curl -X POST -H "X-Tenant: globex" -H "Content-Type: application/json" http://localhost:5106/products \
  -d '{"sku":"UMB-3","name":"Umbrella","price":12,"stock":5,"tenantId":"acme"}'   # 201, stamped globex – tenantId is ignored
curl -i http://localhost:5106/products                                # 404 – "The request has no tenant…"
curl -H "X-Tenant: acme" http://localhost:5106/reports/stock          # sql-query on acme's connection

curl http://localhost:5106/api/admin/tenants/acme/endpoints           # acme's endpoints only
curl http://localhost:5106/api/admin/endpoints/tenants                # tenants that own endpoints
curl http://localhost:5106/openapi/acme/dynamic.json                  # shared + acme's endpoints
```

## Read more

- [Multi-tenancy](../../docs/articles/multi-tenancy.md) – resolvers, isolation, admin API, drafts/history/export per tenant, OpenAPI
- [CRUD on EF Core entities: multi-tenancy](../../docs/articles/ef-crud.md#multi-tenancy) ·
  [Built-in processors: with multi-tenancy](../../docs/articles/built-in-processors.md#with-multi-tenancy) ·
  [Admin panel: a panel per tenant](../../docs/articles/admin-ui.md#a-panel-per-tenant) ·
  [Caching & rate limits with tenants](../../docs/articles/caching-and-rate-limits.md#with-multi-tenancy)
- Next: [07 – OpenAPI import](../07-OpenApiImport/README.md), or the [overview of all samples](../../docs/articles/samples.md).
