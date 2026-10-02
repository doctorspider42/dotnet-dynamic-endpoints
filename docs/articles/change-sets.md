# Saving endpoints in your own transaction: `BeginChanges`

When an endpoint belongs to a row of your own (a feature version, a tenant setting), save both atomically. A change set writes
through the store you give it and touches the routing table only when you apply it, after your commit:

```csharp
var changes = manager.BeginChanges(db.GetDynamicEndpointStore());   // EF Core: tracked by your DbContext, not saved
await changes.UpsertAsync(definition, ct);                          // validated and compiled right away
db.FeatureVersions.Add(version);
await db.SaveChangesAsync(ct);                                      // one SaveChanges, one transaction
await changes.ApplyAsync(ct);                                       // routing table, change handlers, other instances
```

- **Rollback:** drop the change set. Nothing was routed, so there's nothing to undo.
- **Concurrency:** a stale revision fails inside *your* `SaveChanges` with `DbUpdateConcurrencyException`.
- **Explicit transactions:** `manager.BeginChanges(HttpContext.RequestServices)` uses the registered store with *your* scoped
  DbContext, and its saves join `db.Database.BeginTransactionAsync()`. `db.GetDynamicEndpointStore(saveChanges: true)` does the same
  for any context.
- **Route conflicts** are checked across the whole change set too.
- **Drafts:** `SaveDraftAsync`, `DiscardDraftAsync`, `PublishAsync` and `RollbackAsync` are on the change set too, so a draft
  can be saved or published together with your own data ([drafts, history & rollback](drafts-and-history.md)). With EF Core the
  revision is saved in the same `SaveChanges` as the definition.
