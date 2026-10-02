# EF Core: migrations

**Your own DbContext** (recommended): add the table to your model, and your migrations create and evolve it.

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder) =>
    modelBuilder.ApplyDynamicEndpointsConfiguration();   // or ApplyConfiguration(new DynamicEndpointRecordConfiguration(table, schema))
```

```bash
dotnet ef migrations add AddDynamicEndpoints
```

**The bundled `DynamicEndpointsDbContext`** ships its own provider-independent migrations:

```csharp
builder.Services.AddDynamicEndpoints()
    .UseEntityFrameworkStore(o => o.UseNpgsql(connectionString), migrateOnStartup: true);
// or apply them in your deployment step: await db.Database.MigrateAsync();
```

A table created earlier with `EnsureCreated` is adopted into the migration history on the first `migrateOnStartup`. With your own
context, add the table to an empty initial migration the usual EF Core way. `MigrateOnStartup<TContext>()` applies your own
context's migrations on start-up, and any `IDynamicEndpointStoreInitializer` runs before definitions are loaded.
