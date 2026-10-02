using DynamicEndpoints;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

public static class DynamicEndpointsTestingExtensions
{
    /// <summary>
    /// Swaps the persistence of dynamic endpoints for an in-memory store with history and drafts (no database needed): removes
    /// store initializers (e.g. migrations), change notifiers, polling and the scheduled publishing of drafts (call
    /// <see cref="IDynamicEndpointManager.PublishDueAsync"/> instead). Call it after the application's own registrations, e.g. in
    /// <c>ConfigureTestServices</c>. Pass a <paramref name="store"/> to share it between hosts or to inspect it.
    /// </summary>
    public static IDynamicEndpointsBuilder UseInMemoryDynamicEndpoints(this IServiceCollection services, InMemoryDynamicEndpointStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.Replace(ServiceDescriptor.Singleton<IDynamicEndpointStore>(store ?? new InMemoryDynamicEndpointStore()));
        services.RemoveAll<IDynamicEndpointStoreInitializer>();
        services.RemoveAll<IDynamicEndpointChangeNotifier>();
        services.PostConfigure<DynamicEndpointsOptions>(o =>
        {
            o.RefreshInterval = null;
            o.ScheduledPublishInterval = null; // deterministic: tests call IDynamicEndpointManager.PublishDueAsync()
        });
        return new TestBuilder(services);
    }

    /// <summary>
    /// A factory whose application keeps dynamic endpoints in memory – see <see cref="UseInMemoryDynamicEndpoints"/>.
    /// <paramref name="configure"/> can register test processors, validators or filters.
    /// </summary>
    public static WebApplicationFactory<TEntryPoint> WithInMemoryDynamicEndpoints<TEntryPoint>(
        this WebApplicationFactory<TEntryPoint> factory,
        Action<IDynamicEndpointsBuilder>? configure = null,
        InMemoryDynamicEndpointStore? store = null)
        where TEntryPoint : class =>
        factory.WithWebHostBuilder(web => web.ConfigureTestServices(services =>
        {
            var builder = services.UseInMemoryDynamicEndpoints(store);
            configure?.Invoke(builder);
        }));

    /// <summary>The application's <see cref="IDynamicEndpointManager"/>.</summary>
    public static IDynamicEndpointManager GetDynamicEndpointManager<TEntryPoint>(this WebApplicationFactory<TEntryPoint> factory)
        where TEntryPoint : class =>
        factory.Services.GetRequiredService<IDynamicEndpointManager>();

    /// <summary>Creates (or replaces) a dynamic endpoint in the application and returns the stored definition.</summary>
    public static Task<DynamicEndpointDefinition> AddDynamicEndpointAsync<TEntryPoint>(
        this WebApplicationFactory<TEntryPoint> factory,
        DynamicEndpointDefinition definition,
        CancellationToken cancellationToken = default)
        where TEntryPoint : class =>
        factory.GetDynamicEndpointManager().UpsertAsync(definition, cancellationToken);

    private sealed class TestBuilder(IServiceCollection services) : IDynamicEndpointsBuilder
    {
        public IServiceCollection Services { get; } = services;
    }
}
