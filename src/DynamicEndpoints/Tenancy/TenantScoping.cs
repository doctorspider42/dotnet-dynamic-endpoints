using DynamicEndpoints.Management;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicEndpoints;

public static class DynamicEndpointTenantExtensions
{
    /// <summary>
    /// A view of the manager that sees and changes only the endpoints of <paramref name="tenant"/> (<c>null</c>: the shared ones).
    /// Definitions it creates or updates are assigned to the tenant; endpoints of other tenants look like they don't exist.
    /// Hand it to tenant-facing code, e.g. a tenant's own admin API.
    /// </summary>
    public static IDynamicEndpointManager ForTenant(this IDynamicEndpointManager manager, string? tenant)
    {
        ArgumentNullException.ThrowIfNull(manager);
        return new TenantDynamicEndpointManager(manager, CheckTenant(tenant));
    }

    /// <summary>
    /// A view of the store with only the endpoints of <paramref name="tenant"/> (<c>null</c>: the shared ones). Writes of
    /// definitions that belong to another tenant are rejected.
    /// </summary>
    public static IDynamicEndpointStore ForTenant(this IDynamicEndpointStore store, string? tenant)
    {
        ArgumentNullException.ThrowIfNull(store);
        return new TenantDynamicEndpointStore(store, CheckTenant(tenant));
    }

    private static string? CheckTenant(string? tenant) =>
        tenant is null || DynamicEndpointsTenancyOptions.IsValidTenant(tenant)
            ? tenant
            : throw new ArgumentException($"'{tenant}' is not a valid tenant id.", nameof(tenant));
}

internal sealed class TenantDynamicEndpointManager(IDynamicEndpointManager inner, string? tenant) : IDynamicEndpointManager
{
    public string? Tenant => tenant;

    public IReadOnlyList<DynamicProcessorDescriptor> Processors => inner.Processors;

    public IReadOnlyList<DynamicValidatorDescriptor> Validators => inner.Validators;

    public async Task<IReadOnlyList<DynamicEndpointState>> ListAsync(CancellationToken cancellationToken = default) =>
        (await inner.ListAsync(cancellationToken)).Where(s => Owns(s.Definition)).ToList();

    public async Task<DynamicEndpointState?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        await inner.GetAsync(id, cancellationToken) is { } state && Owns(state.Definition) ? state : null;

    public Task<DynamicEndpointDefinition> CreateAsync(DynamicEndpointDefinition definition, CancellationToken cancellationToken = default) =>
        inner.CreateAsync(Stamp(definition), cancellationToken);

    public async Task<DynamicEndpointDefinition> UpdateAsync(DynamicEndpointDefinition definition, CancellationToken cancellationToken = default)
    {
        await EnsureOwnedAsync(definition.Id, cancellationToken);
        return await inner.UpdateAsync(Stamp(definition), cancellationToken);
    }

    public async Task<DynamicEndpointDefinition> UpsertAsync(DynamicEndpointDefinition definition, CancellationToken cancellationToken = default)
    {
        if (definition.Id != Guid.Empty && await inner.GetAsync(definition.Id, cancellationToken) is { } existing && !Owns(existing.Definition))
        {
            throw new DynamicEndpointValidationException("id", $"An endpoint with id '{definition.Id}' already exists.");
        }

        return await inner.UpsertAsync(Stamp(definition), cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        await GetAsync(id, cancellationToken) is not null && await inner.DeleteAsync(id, cancellationToken);

    public async Task<DynamicEndpointDefinition> SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default)
    {
        await EnsureOwnedAsync(id, cancellationToken);
        return await inner.SetEnabledAsync(id, enabled, cancellationToken);
    }

    public Task<DynamicEndpointValidationResult> ValidateAsync(DynamicEndpointDefinition definition, CancellationToken cancellationToken = default) =>
        inner.ValidateAsync(Stamp(definition), cancellationToken);

    public DynamicEndpointChangeSet BeginChanges(IDynamicEndpointStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var scoped = store.ForTenant(tenant);
        return inner is DynamicEndpointManager manager ? manager.BeginChanges(scoped, Stamp) : inner.BeginChanges(scoped);
    }

    public DynamicEndpointChangeSet BeginChanges(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return BeginChanges(services.GetRequiredService<IDynamicEndpointStore>());
    }

    public Task ReloadAsync(CancellationToken cancellationToken = default) => inner.ReloadAsync(cancellationToken);

    private bool Owns(DynamicEndpointDefinition definition) => DynamicEndpointsTenancyOptions.SameTenant(definition.Tenant, tenant);

    private DynamicEndpointDefinition Stamp(DynamicEndpointDefinition definition) =>
        definition.Tenant == tenant ? definition : definition with { Tenant = tenant };

    private async Task EnsureOwnedAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await GetAsync(id, cancellationToken) is null)
        {
            throw new DynamicEndpointNotFoundException(id);
        }
    }
}

internal sealed class TenantDynamicEndpointStore(IDynamicEndpointStore inner, string? tenant) : IDynamicEndpointStore
{
    public async Task<IReadOnlyList<DynamicEndpointDefinition>> GetAllAsync(CancellationToken cancellationToken) =>
        (await inner.GetAllAsync(cancellationToken)).Where(Owns).ToList();

    public async Task<DynamicEndpointDefinition?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await inner.FindAsync(id, cancellationToken) is { } definition && Owns(definition) ? definition : null;

    public async Task AddAsync(DynamicEndpointDefinition definition, CancellationToken cancellationToken)
    {
        Check(definition);
        if (await inner.FindAsync(definition.Id, cancellationToken) is not null)
        {
            throw new DynamicEndpointValidationException("id", $"An endpoint with id '{definition.Id}' already exists.");
        }

        await inner.AddAsync(definition, cancellationToken);
    }

    public async Task UpdateAsync(DynamicEndpointDefinition definition, int expectedRevision, CancellationToken cancellationToken)
    {
        Check(definition);
        if (await FindAsync(definition.Id, cancellationToken) is null)
        {
            throw new DynamicEndpointNotFoundException(definition.Id);
        }

        await inner.UpdateAsync(definition, expectedRevision, cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
        await FindAsync(id, cancellationToken) is not null && await inner.DeleteAsync(id, cancellationToken);

    private bool Owns(DynamicEndpointDefinition definition) => DynamicEndpointsTenancyOptions.SameTenant(definition.Tenant, tenant);

    private void Check(DynamicEndpointDefinition definition)
    {
        if (!Owns(definition))
        {
            throw new DynamicEndpointValidationException("tenant", tenant is null
                ? "The endpoint must be a shared endpoint (no tenant)."
                : $"The endpoint must belong to tenant '{tenant}'.");
        }
    }
}
