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
    /// Replaces a definition. <see cref="DynamicEndpointDefinition.Version"/> must match the stored version
    /// (optimistic concurrency), the returned definition carries the new one.
    /// </summary>
    /// <exception cref="DynamicEndpointValidationException" />
    /// <exception cref="DynamicEndpointNotFoundException" />
    /// <exception cref="DynamicEndpointConcurrencyException" />
    Task<DynamicEndpointDefinition> UpdateAsync(DynamicEndpointDefinition definition, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    Task<DynamicEndpointDefinition> SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>Dry run – checks a definition without saving it.</summary>
    Task<DynamicEndpointValidationResult> ValidateAsync(DynamicEndpointDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>Re-reads all definitions from the store and rebuilds the routing table of this instance.</summary>
    Task ReloadAsync(CancellationToken cancellationToken = default);
}
