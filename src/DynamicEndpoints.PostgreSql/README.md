# DynamicEndpoints.PostgreSql

Instant propagation of [DynamicEndpoints](https://www.nuget.org/packages/DynamicEndpoints) changes across instances through
PostgreSQL `LISTEN/NOTIFY`. When one instance publishes, disables or deletes an endpoint, the others reload right away instead of
waiting for the next poll. If your definitions already live in PostgreSQL, you need no extra infrastructure.

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/doctorspider42/dotnet-dynamic-endpoints/blob/main/LICENSE)

```csharp
builder.Services.AddDynamicEndpoints(o => o.RefreshInterval = TimeSpan.FromMinutes(5)) // fallback only
    .UseEntityFrameworkStore<AppDbContext>()
    .UsePostgreSqlChangeNotifications(connectionString);
```

Without a connection string it uses the `NpgsqlDataSource` registered in DI (`AddNpgsqlDataSource`):

```csharp
builder.Services.AddNpgsqlDataSource(connectionString);
builder.Services.AddDynamicEndpoints().UsePostgreSqlChangeNotifications(o => o.Channel = "orders_endpoints");
```

## How it works

- After a change is applied, the instance sends `pg_notify(channel, { instanceId, endpointIds })`.
- Every instance keeps one connection open with `LISTEN`. It checks the connection every `KeepAliveInterval` (30 s), and after a
  reconnect it reloads, in case it missed something.
- Notifications only trigger a reload from the store, so a lost one costs nothing but time. Keep `RefreshInterval` as the fallback.

📖 [Documentation](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/multiple-instances.html) ·
📝 [Changelog](https://github.com/doctorspider42/dotnet-dynamic-endpoints/blob/main/CHANGELOG.md)
