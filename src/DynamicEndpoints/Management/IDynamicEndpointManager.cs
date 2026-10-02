namespace DynamicEndpoints;

/// <summary>
/// The single entry point for managing dynamic endpoints. Every change is validated, persisted and applied
/// to the routing table of this instance immediately.
/// </summary>
public interface IDynamicEndpointManager
{
    /// <summary>Processors admins can choose from.</summary>
    IReadOnlyList<DynamicProcessorDescriptor> Processors { get; }

    /// <summary>Custom validators admins can attach to parameters and endpoints.</summary>
    IReadOnlyList<DynamicValidatorDescriptor> Validators { get; }

    Task<IReadOnlyList<DynamicEndpointState>> ListAsync(CancellationToken cancellationToken = default);

    Task<DynamicEndpointState?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <exception cref="DynamicEndpointValidationException">The definition is invalid or clashes with another route.</exception>
    Task<DynamicEndpointDefinition> CreateAsync(DynamicEndpointDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a definition. <see cref="DynamicEndpointDefinition.Revision"/> must match the stored revision
    /// (optimistic concurrency), the returned definition carries the new one.
    /// </summary>
    /// <exception cref="DynamicEndpointValidationException" />
    /// <exception cref="DynamicEndpointNotFoundException" />
    /// <exception cref="DynamicEndpointConcurrencyException" />
    Task<DynamicEndpointDefinition> UpdateAsync(DynamicEndpointDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the definition, or replaces the stored one with the same <see cref="DynamicEndpointDefinition.Id"/> without a
    /// revision check (last writer wins) – handy for syncing definitions from your own model. When the stored definition already has
    /// the same content nothing is written and the revision stays.
    /// </summary>
    /// <exception cref="DynamicEndpointValidationException" />
    Task<DynamicEndpointDefinition> UpsertAsync(DynamicEndpointDefinition definition, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    Task<DynamicEndpointDefinition> SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>Dry run – checks a definition without saving it.</summary>
    Task<DynamicEndpointValidationResult> ValidateAsync(DynamicEndpointDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a unit of work whose changes are written through <paramref name="store"/> – e.g. one bound to your own <c>DbContext</c>
    /// (<c>db.GetDynamicEndpointStore()</c> in the EF Core package), so they are saved by your <c>SaveChanges</c> in your transaction.
    /// The routing table changes only when you call <see cref="DynamicEndpointChangeSet.ApplyAsync"/> after the commit.
    /// </summary>
    DynamicEndpointChangeSet BeginChanges(IDynamicEndpointStore store);

    /// <summary>
    /// Like <see cref="BeginChanges(IDynamicEndpointStore)"/>, with the store resolved from <paramref name="services"/> – pass the request
    /// scope (<c>HttpContext.RequestServices</c>) so a DbContext-based store shares the DbContext (and its transaction) with your code.
    /// </summary>
    DynamicEndpointChangeSet BeginChanges(IServiceProvider services);

    /// <summary>Re-reads all definitions from the store and rebuilds the routing table of this instance.</summary>
    Task ReloadAsync(CancellationToken cancellationToken = default);
}
