namespace DynamicEndpoints;

/// <summary>
/// Persistence of endpoint definitions. Resolved from a fresh DI scope for every operation,
/// so implementations may be scoped (e.g. backed by a DbContext).
/// </summary>
public interface IDynamicEndpointStore
{
    Task<IReadOnlyList<DynamicEndpointDefinition>> GetAllAsync(CancellationToken cancellationToken);

    Task<DynamicEndpointDefinition?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task AddAsync(DynamicEndpointDefinition definition, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the stored definition. Must throw <see cref="DynamicEndpointConcurrencyException"/> when the stored
    /// revision differs from <paramref name="expectedRevision"/> and <see cref="DynamicEndpointNotFoundException"/> when it does not exist.
    /// </summary>
    Task UpdateAsync(DynamicEndpointDefinition definition, int expectedRevision, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>
/// Prepares the store before definitions are loaded on start-up – e.g. applies database migrations. Initializers are resolved
/// from a scope and run in registration order; a failure fails the start (unless <see cref="DynamicEndpointsOptions.ThrowOnStartupLoadFailure"/> is off).
/// </summary>
public interface IDynamicEndpointStoreInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken);
}
