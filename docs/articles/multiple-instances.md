# Multiple instances

Each instance keeps its own routing table. A change notifier tells the others right away, and polling catches whatever a
notifier missed:

```csharp
builder.Services.AddDynamicEndpoints(o => o.RefreshInterval = TimeSpan.FromMinutes(5))   // fallback only
    .UseEntityFrameworkStore<AppDbContext>()
    .UsePostgreSqlChangeNotifications(connectionString);    // DynamicEndpoints.PostgreSql: LISTEN/NOTIFY, no extra infrastructure
 // .UseRedisChangeNotifications("redis:6379");             // DynamicEndpoints.Redis: pub/sub
 // .UseChangeNotifier<MyServiceBusNotifier>();             // or your own IDynamicEndpointChangeNotifier
```

- **Instant:** every change is published after it was applied. The other instances reload at once, and bursts are coalesced.
- **Resilient:** listeners reconnect on their own and reload after every reconnect, in case something was missed while
  disconnected. An instance ignores its own messages.
- **Push by hand:** `IDynamicEndpointManager.ReloadAsync()` still works from any signal of yours.
- **Visibility:** the admin list reports `Pending` for changes this instance hasn't picked up yet.
- **Conflicting writes:** a stale write from another instance is rejected by the database concurrency token.
- **Scheduled drafts** are published once, by whichever instance is first; the others see the draft gone and skip it
  ([drafts](drafts-and-history.md)).
- **Output caches** are evicted on every instance when a definition changes. **Rate limit counters** are kept per instance, so
  with N instances a client can get up to N times the limit ([caching & rate limits](caching-and-rate-limits.md)).
- **See it run:** the [Aspire AppHost](aspire-and-demo.md) starts the sample twice on PostgreSQL with Redis notifications.
