# DynamicEndpoints.EntityFrameworkCore

Entity Framework Core persistence for [DynamicEndpoints](https://www.nuget.org/packages/DynamicEndpoints):
runtime-defined HTTP endpoints for ASP.NET Core. With it, endpoint definitions survive restarts and are shared
by every instance of your application.

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/doctorspider42/dotnet-dynamic-endpoints/blob/main/LICENSE)

## Use your own DbContext

```csharp
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyDynamicEndpointsConfiguration(); // DynamicEndpoints, DynamicEndpointRevisions, DynamicEndpointDrafts
        // or: modelBuilder.ApplyConfiguration(new DynamicEndpointRecordConfiguration("endpoints", "api"));
}

builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));
builder.Services.AddDynamicEndpoints().UseEntityFrameworkStore<AppDbContext>();
```

Create the tables with your migrations as usual (`dotnet ef migrations add AddDynamicEndpoints`). Upgrading from 0.3, the
history and draft tables are new: add a migration, or call `ApplyDynamicEndpointsConfiguration(history: false)` to go without them. `.MigrateOnStartup<AppDbContext>()`
applies them on start-up if you want that.

## …or the bundled context, with its own migrations

```csharp
builder.Services.AddDynamicEndpoints()
    .UseEntityFrameworkStore(o => o.UseNpgsql(connectionString), migrateOnStartup: true); // DynamicEndpointsDbContext
```

The migrations have no provider-specific column types, so they work with SQL Server, PostgreSQL, SQLite, MySQL, … A table
created earlier with `EnsureCreated` is adopted into the migration history. If you'd rather migrate in your deployment pipeline,
leave `migrateOnStartup` off and call `Database.MigrateAsync()` on `DynamicEndpointsDbContext` there.

## Save endpoints together with your own data

```csharp
var changes = manager.BeginChanges(db.GetDynamicEndpointStore());   // tracked by db, not saved
await changes.UpsertAsync(definition, ct);
db.FeatureVersions.Add(version);
await db.SaveChangesAsync(ct);                                      // one SaveChanges, one transaction
await changes.ApplyAsync(ct);                                       // now the routes change
```

If you drop the change set instead of saving, nothing is routed. A stale revision fails inside your `SaveChanges` with
`DbUpdateConcurrencyException`. If you use explicit transactions, `manager.BeginChanges(HttpContext.RequestServices)` saves
through your scoped DbContext, inside `db.Database.BeginTransactionAsync()`.

## Your entities as CRUD endpoints: `ef-crud`

```csharp
builder.Services.AddDynamicEndpoints()
    .AddEntityFrameworkCrud<AppDbContext>(crud => crud
        .Entity<Product>(e => e                       // "products" – the DbSet property name
            .Fields(p => p.Id, p => p.Sku, p => p.Name, p => p.Price)
            .ReadOnly(p => p.CreatedAt)
            .Filterable(p => p.Name, p => p.Price)
            .Sortable(p => p.Name)
            .TenantColumn(p => p.TenantId)
            .Operations(CrudOperations.All & ~CrudOperations.Delete))
        .Entity<Order>("orders", e => e.AllFields(except: o => o.InternalNote)));
```

Admins then build list, get, create, update, patch and delete endpoints on those entities – `"processor": "ef-crud",
"processorConfig": { "entity": "products", "operation": "list", "pageSize": 50, "sort": "name" }` – or scaffold all of them from
the EF model (`POST /scaffold/crud?entity=products&dryRun=true` in the admin API, *Scaffold CRUD* in the panel,
`IDynamicCrudScaffolder` in code).

- **Allowlist:** only exposed entities and fields; definitions store the entity name, never a type. Request bodies are never bound
  to entities – only declared body parameters that are writable fields are written. Keys, generated values, concurrency tokens,
  shadow properties and the tenant column are read-only; navigations are never exposed. `AllFields()` also exposes properties
  added later.
- **Lists:** `page`/`pageSize` with a `maxPageSize`, sorting and filters (`eq`, `ne`, `lt`, `lte`, `gt`, `gte`, `contains`,
  `startsWith`, `in`) on allowlisted fields only, built as expression trees with parameters.
- **Concurrency:** `ETag` from the concurrency token, `If-Match` on update, patch and delete (`412`, `428` with `requireIfMatch`).
- **Tenants:** `TenantColumn(…)` filters every query by the endpoint's (or request's) tenant and stamps new rows; entities without
  one are for shared endpoints only, unless `SharedAcrossTenants()` or `AllowTenants(…)`.
- **Hooks:** `IDynamicCrudInterceptor<TEntity>` in DI, before and after create, update and delete, can reject with validation errors.
- **Code:** `DynamicEndpoint.Get("/products").HandledByCrud<Product>(CrudOperation.List, c => c.PageSize = 20)`.

📖 [CRUD on EF Core entities](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/ef-crud.html)

## How it is stored

- **Columns:** the searchable fields (method, route, name, enabled, version, timestamps).
- **JSON:** the full definition, so the definition model can grow without schema migrations.
- **Concurrency:** `Version` is a concurrency token, so a stale write from another instance or admin is rejected.
- **History and drafts:** one row per revision (`DynamicEndpointRevisions`) and per draft (`DynamicEndpointDrafts`). A revision is
  saved in the same `SaveChanges` as the definition it belongs to, and a publish removes its draft in it too.
- **Providers:** any EF Core relational provider works (SQL Server, PostgreSQL, SQLite, MySQL, …).

📖 [Documentation](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/ef-core.html) ·
📝 [Changelog](https://github.com/doctorspider42/dotnet-dynamic-endpoints/blob/main/CHANGELOG.md)
