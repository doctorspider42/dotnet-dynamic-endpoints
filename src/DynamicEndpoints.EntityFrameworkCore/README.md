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
}

builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlServer(connectionString));
builder.Services.AddDynamicEndpoints().UseEntityFrameworkStore<AppDbContext>();
```

Create the table with your migrations as usual.

## …or the bundled context

```csharp
builder.Services.AddDynamicEndpoints()
    .UseEntityFrameworkStore(o => o.UseNpgsql(connectionString)); // DynamicEndpointsDbContext
```

## How it is stored

- **Columns:** the searchable fields (method, route, name, enabled, version, timestamps).
- **JSON:** the full definition, so the definition model can grow without schema migrations.
- **Concurrency:** `Version` is a concurrency token, so a stale write from another instance or admin is rejected.
- **Providers:** any EF Core relational provider works (SQL Server, PostgreSQL, SQLite, MySQL, …).

📖 [Documentation](https://github.com/doctorspider42/dotnet-dynamic-endpoints#readme) ·
📝 [Changelog](https://github.com/doctorspider42/dotnet-dynamic-endpoints/blob/main/CHANGELOG.md)
