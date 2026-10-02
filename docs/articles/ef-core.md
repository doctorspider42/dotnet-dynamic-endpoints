# EF Core: migrations

**Your own DbContext** (recommended): add the table to your model, and your migrations create and evolve it.

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder) =>
    modelBuilder.ApplyDynamicEndpointsConfiguration();   // or ApplyConfiguration(new DynamicEndpointRecordConfiguration(table, schema))
```

```bash
dotnet ef migrations add AddDynamicEndpoints
```

`ApplyDynamicEndpointsConfiguration()` maps three tables: `DynamicEndpoints`, plus `DynamicEndpointRevisions` and
`DynamicEndpointDrafts` for [history and drafts](drafts-and-history.md). Upgrading from 0.3, add a migration for the two new ones,
or pass `history: false` to keep the old model (and no history and drafts). A revision is saved in the same `SaveChanges` as the
definition it belongs to, and a publish removes its draft in it too.

**The bundled `DynamicEndpointsDbContext`** ships its own provider-independent migrations, the history and draft tables included:

```csharp
builder.Services.AddDynamicEndpoints()
    .UseEntityFrameworkStore(o => o.UseNpgsql(connectionString), migrateOnStartup: true);
// or apply them in your deployment step: await db.Database.MigrateAsync();
```

A table created earlier with `EnsureCreated` is adopted into the migration history on the first `migrateOnStartup`.
Definitions that existed before the history tables start their history with their published revision. With your own
context, add the table to an empty initial migration the usual EF Core way. `MigrateOnStartup<TContext>()` applies your own
context's migrations on start-up, and any `IDynamicEndpointStoreInitializer` runs before definitions are loaded.

## Your entities as endpoints

The same package has the [`ef-crud` processor](ef-crud.md): `AddEntityFrameworkCrud<AppDbContext>(…)` lets admins build – or
scaffold from the model – list, get, create, update, patch and delete endpoints on the entities and fields you allowlist, with
ETags, tenant columns and interceptors. It works on any context, with or without the definition tables; it needs no schema of its own.
