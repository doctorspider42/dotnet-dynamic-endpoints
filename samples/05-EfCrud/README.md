# 05 – CRUD on EF Core entities (`ef-crud`)

The quick path, **without tenancy**: you allowlist entities and fields of your `DbContext`, admins build – or scaffold from the
EF model in one click – list, get, create, update, patch and delete endpoints on them. Paging, safe filters and sorting, ETags
with `If-Match`, and an interceptor for the server-side rules. No mass assignment: only declared body parameters that are
writable fields are written. The same with tenant columns is in [06 – Multi-tenancy](../06-MultiTenancy/README.md).

| File | What to look at |
|---|---|
| [`Program.cs`](Program.cs) | `AddEntityFrameworkCrud<AppDbContext>(…)`: `Fields` / `ReadOnly` / `Filterable` / `Sortable` for products, `AllFields(except: …)` and `Operations(CrudOperations.Read)` for categories |
| [`AppDbContext.cs`](AppDbContext.cs) | `HasMaxLength` and `HasPrecision` become parameter limits, the `IsConcurrencyToken()` becomes the ETag, `PurchasePrice` and `InternalNote` never leave the database |
| [`ProductRules.cs`](ProductRules.cs) | an `IDynamicCrudInterceptor<Product>`: sets `CreatedAt`, rejects negative prices and stock |
| [`EfCrudSeeder.cs`](EfCrudSeeder.cs) | `IDynamicCrudScaffolder.ScaffoldAsync("products", …)` and hand-written endpoints with `HandledByCrud<Category>(…)` / `HandledByCrud<Product>(…)` |
| [`ef-crud.http`](ef-crud.http) | the requests below |

| Endpoint | |
|---|---|
| `GET /shop/products?page=&pageSize=&sort=&sku=&name=&price=&minPrice=&maxPrice=` | scaffolded list: `{ items, page, pageSize, total }` |
| `GET` · `PUT` · `PATCH` · `DELETE /shop/products/{id}`, `POST /shop/products` | scaffolded; get, create, update and patch answer with an `ETag` |
| `PATCH /shop/products/{id}/stock` | by hand, `RequireIfMatch`: `428` without `If-Match`, `412` with a stale ETag |
| `GET /shop/categories?q=&active=`, `GET /shop/categories/{id}` | by hand: a `contains` filter, a boolean filter, a default sort |

## Run it

```bash
dotnet run --project samples/05-EfCrud
```

Definitions, products and categories in `ef-crud.db` (in the sample's folder, the working directory of `dotnet run`). Panel:
<http://localhost:5105/admin/>, Swagger UI: <http://localhost:5105/swagger> – the response schemas come from the live EF model.

## Click around

- **Scaffold CRUD** (in the toolbar above the list): pick `categories`, route prefix `/catalog/categories`, dry run – the wizard lists
  the endpoints it would create (only list and get: `Operations(CrudOperations.Read)`), with their definitions. Try the prefix
  `/shop/categories`: both are *skipped*, the routes exist.
- Open *Set stock (If-Match required)*: the configuration is the ef-crud form – entity, operation, a table of the entity's fields
  (type, writable, filter operators, sortable).
- Add a body parameter `purchasePrice` to the scaffolded *create* endpoint and save: rejected, the field isn't exposed.

## Call it

```bash
curl "http://localhost:5105/shop/products?sort=-price"                 # 4 products, no purchasePrice
curl "http://localhost:5105/shop/products?maxPrice=20&sort=name"       # Giant magnet, Hammer
curl "http://localhost:5105/shop/products?sort=stock"                  # 400 – stock isn't sortable

curl -i -X POST http://localhost:5105/shop/products -H "Content-Type: application/json" \
  -d '{"sku":"TNT-5","name":"Dynamite","price":9.5,"stock":100,"id":999,"purchasePrice":1}'
# 201, Location: /shop/products/5, ETag – "id" and "purchasePrice" were ignored, createdAt set by ProductRules
curl -X POST http://localhost:5105/shop/products -H "Content-Type: application/json" -d '{"sku":"NEG-1","name":"Debt","price":-1,"stock":1}'
# 400 – "The price can't be negative." (the interceptor)

curl -i http://localhost:5105/shop/products/1                          # ETag: "…"
curl -X PATCH http://localhost:5105/shop/products/1/stock -H "Content-Type: application/json" -d '{"stock":5}'          # 428
curl -X PATCH http://localhost:5105/shop/products/1/stock -H 'If-Match: "<etag>"' -H "Content-Type: application/json" -d '{"stock":5}'  # 200

curl "http://localhost:5105/shop/categories?q=ool"                     # Tools, without internalNote
curl http://localhost:5105/api/admin/endpoints/crud/entities           # what admins may use
```

## Read more

- [CRUD on EF Core entities](../../docs/articles/ef-crud.md) – the allowlist, operations, filters, values, ETags, interceptors,
  scaffolding, OpenAPI
- [EF Core & migrations](../../docs/articles/ef-core.md) · [Admin panel](../../docs/articles/admin-ui.md) (ef-crud form, Scaffold
  CRUD) · [Security](../../docs/articles/security.md)
- Next: [06 – Multi-tenancy](../06-MultiTenancy/README.md), or the [overview of all samples](../../docs/articles/samples.md).
