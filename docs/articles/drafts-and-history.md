# Drafts, history & rollback

A change doesn't have to go live right away. Save it as a **draft**: it's validated like any definition, but routing keeps
serving the published revision until you publish the draft, by hand or at a set time.

```csharp
var draft = await manager.SaveDraftAsync(current with { Route = "/v2/orders" });   // based on current.Revision
await manager.SaveDraftAsync(DynamicEndpoint.Get("/promo").HandledBy("echo"),      // a new endpoint, drafted…
    publishAt: new DateTimeOffset(2026, 12, 24, 18, 0, 0, TimeSpan.Zero), comment: "Christmas promo");   // …and scheduled
var changes = await manager.DiffDraftAsync(draft.EndpointId);                      // what publishing would change
await manager.PublishAsync(draft.EndpointId);                                      // live – as the next revision

var history = await manager.GetHistoryAsync(id);                                   // every revision, newest first
var diff = await manager.DiffAsync(id, fromRevision: 3, toRevision: 5);            // [{ path: "parameters[quantity].maximum", kind: "Changed", from: 10, to: 100 }]
await manager.RollbackAsync(id, revision: 3);                                      // revision 3's content as revision 6
```

`SaveDraftAsync(definition, publishAt, comment)` is a shortcut for `SaveDraftAsync(new DynamicEndpointDraft { … })`.

- **History:** every create, update, enable/disable, publish and rollback is a revision (`DynamicEndpointRevision`) with its kind
  (`Created`, `Updated`, `Enabled`, `Disabled`, `Published`, `RolledBack`) and comment. A rollback adds a revision instead of
  rewriting history, and its comment says where it came from (`SourceRevision`). Deleting an endpoint deletes its history and draft.
- **Drafts:** at most one per endpoint, saving again replaces it. `ListAsync` shows them (`state.Draft`), and never-published
  endpoints have the status `Draft`. A draft remembers the revision it's based on (`BaseRevision`). If someone changed the endpoint
  in the meantime, publishing fails with a `409` / `DynamicEndpointConcurrencyException`, so save the draft again on top of the
  current revision. `DiscardDraftAsync` drops it and leaves the published revision alone.
- **Scheduled publishing:** drafts with `PublishAt` are published every `options.ScheduledPublishInterval` (10 s), exactly once
  across instances. A draft that can't be published any more (e.g. its route is taken) is logged and unscheduled; the draft
  itself stays. Call `PublishDueAsync()` from your own scheduler if you turn the interval off (`null`).
- **Diffs** address parameters and validators by name (`parameters[quantity].maximum`), so inserting one doesn't make
  everything look changed. `DynamicEndpointDiff.Compare(a, b)` compares any two definitions.
- **Transactions:** [change sets](change-sets.md) have `SaveDraftAsync`, `PublishAsync`, `RollbackAsync` and `DiscardDraftAsync` too.
- **Stores:** the in-memory and EF Core stores keep history and drafts ([EF Core tables](ef-core.md)). A custom store opts in by
  implementing `IDynamicEndpointRevisionStore`, and without it these calls throw `NotSupportedException` (`501` in the admin API).

## Together with the rest

- **Change events and the audit log:** publishing and rolling back are ordinary changes: they raise `Created` / `Updated`
  [change events](change-events.md), are written to the [audit log](audit-log.md) and reach the other instances. Saving or
  discarding a draft doesn't route anything, so it raises no event.
- **Admin API & panel:** `/drafts`, `/{id}/draft`, `/{id}/publish`, `/{id}/revisions`, `/{id}/diff` and
  `/{id}/revisions/{revision}/rollback` in the [admin API](admin-api.md). The [admin panel](admin-ui.md) has *Save draft* with a
  publish time and a comment, a history tab with diffs and one-click rollback.
- **Multi-tenancy:** `manager.ForTenant("acme")` and a tenant's admin API see only the tenant's drafts and history, and drafts
  they save belong to the tenant. Details in [Multi-tenancy](multi-tenancy.md#drafts-history-export-and-import-per-tenant).
- **Tests:** `DynamicEndpoints.Testing` keeps history and drafts in memory, but doesn't run the scheduler: call
  `manager.PublishDueAsync()` ([Testing](testing.md)).
