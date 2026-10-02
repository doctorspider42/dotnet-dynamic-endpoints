# Managing endpoints: `IDynamicEndpointManager`

Every operation validates, persists and swaps the routing table atomically. Inject it anywhere:

```csharp
await manager.CreateAsync(definition);
await manager.UpdateAsync(definition with { Route = "/v2/orders" });   // optimistic concurrency via Revision
await manager.UpsertAsync(definition);                                 // create or replace by Id, no revision needed
await manager.SetEnabledAsync(id, false);
await manager.DeleteAsync(id);
var check = await manager.ValidateAsync(definition);                   // dry run
await manager.ReloadAsync();                                           // re-read the store
```

`UpsertAsync` is made for syncing definitions from your own model: it creates the endpoint, or replaces the stored one with the same
`Id` whatever its revision. Nothing is written (and the revision stays) when the content didn't change.

The sample's `Greetings/` folder shows a purpose-built API on top of the manager: `POST /api/greetings {"slug":"pirate","greeting":"Ahoy"}` publishes `GET /greetings/pirate/{name}` immediately.
