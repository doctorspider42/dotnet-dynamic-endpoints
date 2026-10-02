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
        modelBuilder.ApplyDynamicEndpointsConfiguration(); // adds the DynamicEndpoints table
        // or: modelBuilder.ApplyConfiguration(new DynamicEndpointRecordConfiguration("endpoints", "api"));
}

builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));
builder.Services.AddDynamicEndpoints().UseEntityFrameworkStore<AppDbContext>();
```

Create the table with your migrations as usual (`dotnet ef migrations add AddDynamicEndpoints`). `.MigrateOnStartup<AppDbContext>()`
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

## How it is stored

- **Columns:** the searchable fields (method, route, name, enabled, version, timestamps).
- **JSON:** the full definition, so the definition model can grow without schema migrations.
- **Concurrency:** `Version` is a concurrency token, so a stale write from another instance or admin is rejected.
- **Providers:** any EF Core relational provider works (SQL Server, PostgreSQL, SQLite, MySQL, …).

📖 [Documentation](https://github.com/doctorspider42/dotnet-dynamic-endpoints#readme) ·
📝 [Changelog](https://github.com/doctorspider42/dotnet-dynamic-endpoints/blob/main/CHANGELOG.md)
