# Testing: `DynamicEndpoints.Testing`

```csharp
// Your application, without its database: in-memory store, no migrations, no notifier, no polling.
await using var factory = new WebApplicationFactory<Program>()
    .WithInMemoryDynamicEndpoints(b => b.AddProcessor("fake-crm", _ => Results.Ok(new { id = 1 })));
await factory.AddDynamicEndpointAsync(DynamicEndpoint.Get("/customers/{id}").HandledBy("fake-crm").FromRoute("id"));
var response = await factory.CreateClient().GetAsync("/customers/1");

// Or just your processors and validators, without the application.
await using var server = await DynamicEndpointsTestServer.StartAsync(b => b.AddProcessor<OrderLookupProcessor>());
await server.AddEndpointAsync(DynamicEndpoint.Get("/orders/{id}").HandledBy<OrderLookupProcessor>().FromRoute("id"));
```

`WithInMemoryDynamicEndpoints` swaps the store for an in-memory one, with history and drafts. It also removes store initializers
(migrations), change notifiers, polling and scheduled publishing: call `manager.PublishDueAsync()` to publish due drafts. Your
seeders still run, against the in-memory store.

`services.UseInMemoryDynamicEndpoints()` does the same in your own `ConfigureTestServices`. To test several instances, share one
`InMemoryDynamicEndpointStore` and one `InMemoryDynamicEndpointChangeNotifier` between servers.
