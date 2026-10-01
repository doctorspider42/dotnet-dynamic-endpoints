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
    /// version differs from <paramref name="expectedVersion"/> and <see cref="DynamicEndpointNotFoundException"/> when it does not exist.
    /// </summary>
    Task UpdateAsync(DynamicEndpointDefinition definition, int expectedVersion, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
}
