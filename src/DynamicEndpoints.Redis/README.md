# DynamicEndpoints.Redis

Instant propagation of [DynamicEndpoints](https://www.nuget.org/packages/DynamicEndpoints) changes across instances through
Redis pub/sub. When one instance publishes, disables or deletes an endpoint, the others reload right away instead of waiting
for the next poll.

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/doctorspider42/dotnet-dynamic-endpoints/blob/main/LICENSE)

```csharp
builder.Services.AddDynamicEndpoints(o => o.RefreshInterval = TimeSpan.FromMinutes(5)) // fallback only
    .UseEntityFrameworkStore<AppDbContext>()
    .UseRedisChangeNotifications("redis:6379");
```

To reuse the application's connection, register an `IConnectionMultiplexer` and leave the configuration out:

```csharp
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect("redis:6379"));
builder.Services.AddDynamicEndpoints().UseRedisChangeNotifications(o => o.Channel = "orders-endpoints");
```

## How it works

- After a change is applied, the instance publishes `{ instanceId, endpointIds }` on the channel (default `dynamic-endpoints`).
- Every instance subscribes, and reloads after every reconnect of the subscription. Pub/sub doesn't keep messages for
  disconnected subscribers.
- Notifications only trigger a reload from the store. Keep `RefreshInterval` as the fallback.

📖 [Documentation](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/multiple-instances.html) ·
📝 [Changelog](https://github.com/doctorspider42/dotnet-dynamic-endpoints/blob/main/CHANGELOG.md)
