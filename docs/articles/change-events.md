# Change events: audit, cache invalidation

```csharp
builder.Services.AddDynamicEndpoints()
    .AddChangeHandler<AuditChangeHandler>()                   // scoped, run in registration order
    .OnChanged((change, ct) => cache.RemoveAsync(change.Id.ToString(), ct));

public sealed class AuditChangeHandler(AuditLog audit) : IDynamicEndpointChangeHandler
{
    public Task OnChangedAsync(DynamicEndpointChangedEvent change, CancellationToken ct) =>
        change.Origin == DynamicEndpointChangeOrigin.Local     // once, on the instance that made the change
            ? audit.WriteAsync(change.Kind, change.Id, change.Previous, change.Definition, ct)
            : Task.CompletedTask;
}
```

- `Kind` is `Created`, `Updated` (including enable/disable) or `Deleted`. `Previous` and `Definition` are the definitions before
  and after the change.
- `Origin` is `Local` for changes made through this instance's manager, raised once. It's `Remote` for changes picked up by a
  reload, raised on every other instance, which is what per-instance caches need.
- For a ready-made audit log (user, diff, sinks, admin API) see [`AddAuditLog()`](audit-log.md).
- Handlers run after the routing table was updated. Exceptions are logged and don't undo the change.
