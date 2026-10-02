# DynamicEndpoints.Testing

Test helpers for [DynamicEndpoints](https://www.nuget.org/packages/DynamicEndpoints). Run your dynamic endpoints in
`WebApplicationFactory`, or on a small test server, without PostgreSQL or any other database.

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/doctorspider42/dotnet-dynamic-endpoints/blob/main/LICENSE)

## Your application, in memory

```csharp
await using var factory = new WebApplicationFactory<Program>()
    .WithInMemoryDynamicEndpoints(b => b.AddProcessor("fake-crm", _ => Results.Ok(new { id = 1 })));

await factory.AddDynamicEndpointAsync(DynamicEndpoint.Get("/customers/{id}").HandledBy("fake-crm").FromRoute("id"));
var response = await factory.CreateClient().GetAsync("/customers/1");
```

`WithInMemoryDynamicEndpoints` swaps the store for an in-memory one, with history and drafts. It also removes store initializers
(migrations), change notifiers, polling and scheduled publishing: call `manager.PublishDueAsync()` to publish due drafts. Your seeders still run, against the in-memory store. In your own `ConfigureTestServices` it's
`services.UseInMemoryDynamicEndpoints()`.

## Just your processors and validators

```csharp
await using var server = await DynamicEndpointsTestServer.StartAsync(b => b
    .AddProcessor<OrderLookupProcessor>()
    .AddValidator<NipValidator>());

await server.AddEndpointAsync(DynamicEndpoint.Get("/orders/{id}").HandledBy<OrderLookupProcessor>().FromRoute("id"));
var order = await server.Client.GetFromJsonAsync<Order>("/orders/42");
var openApi = server.OpenApi.GetDocument();
```

## Several instances

Share one `InMemoryDynamicEndpointStore` and one `InMemoryDynamicEndpointChangeNotifier` between servers:

```csharp
var store = new InMemoryDynamicEndpointStore();
var notifier = new InMemoryDynamicEndpointChangeNotifier();
var options = new DynamicEndpointsTestServerOptions { Store = store, DynamicEndpoints = b => b.UseChangeNotifier(notifier) };
await using var a = await DynamicEndpointsTestServer.StartAsync(options);
await using var b = await DynamicEndpointsTestServer.StartAsync(options);
```

📖 [Documentation](https://github.com/doctorspider42/dotnet-dynamic-endpoints#readme) ·
📝 [Changelog](https://github.com/doctorspider42/dotnet-dynamic-endpoints/blob/main/CHANGELOG.md)
