namespace DynamicEndpoints;

/// <summary>
/// Creates initial endpoints on application start – after persisted definitions were loaded, before requests are served.
/// Resolved from a DI scope, so inject <see cref="IDynamicEndpointManager"/> (or anything else) into the constructor.
/// Register with <c>AddSeeder&lt;T&gt;()</c>; seeders run in registration order and must be idempotent.
/// </summary>
public interface IDynamicEndpointSeeder
{
    Task SeedAsync(CancellationToken cancellationToken);
}
