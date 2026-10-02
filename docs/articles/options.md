# Options

```csharp
builder.Services.AddDynamicEndpoints(o =>
{
    o.ReservedPrefixes.Add("/internal");          // never usable by dynamic endpoints
    o.RequiredRoutePrefixes.Add("/api/v{version:int}");  // every route must start with /api/v1, /api/v2, …
    o.DefaultProcessor = "orders";                // when a definition names none
    o.MaxRequestBodySize = 1024 * 1024;           // JSON bodies, bytes
    o.MaxFormBodySize = 30 * 1024 * 1024;         // form bodies with all their files, bytes
    o.MaxJsonDepth = 32;
    o.RefreshInterval = TimeSpan.FromSeconds(30); // multi-instance polling (the fallback when a change notifier is used)
    o.InstanceId = "api-1";                       // identifies this instance in change notifications (unique by default)
    o.ThrowOnStartupLoadFailure = true;
    o.ConfigureEndpoint = (builder, definition) => { /* extra metadata */ };
    o.OpenApi.Title = "My dynamic API";
    o.OpenApi.DefaultGroup = "Dynamic";
});

app.MapDynamicEndpoints().RequireRateLimiting("api");   // conventions for all dynamic endpoints
```
