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
