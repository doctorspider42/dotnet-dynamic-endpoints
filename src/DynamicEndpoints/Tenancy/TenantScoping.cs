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
        tenant = CheckTenant(tenant);
        return store is IDynamicEndpointRevisionStore
            ? new TenantDynamicEndpointRevisionStore(store, tenant)
            : new TenantDynamicEndpointStore(store, tenant);
    }

    // Replaced services can't be narrowed to a tenant; refusing beats leaking other tenants' endpoints.
    internal static InvalidOperationException NotTenantAware(object service) => new(
        $"{service.GetType().Name} can't be scoped to a tenant, so the tenant admin API can't use it.");

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
        (await inner.ListAsync(cancellationToken)).Where(Owns).ToList();

    public async Task<DynamicEndpointState?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        await inner.GetAsync(id, cancellationToken) is { } state && Owns(state) ? state : null;

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

    public async Task<DynamicEndpointDraft> SaveDraftAsync(DynamicEndpointDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(draft.Definition);
        var id = draft.Definition.Id;
        if (id != Guid.Empty && await inner.GetAsync(id, cancellationToken) is { } existing && !Owns(existing))
        {
            throw new DynamicEndpointValidationException("id", $"An endpoint with id '{id}' already exists.");
        }

        return await inner.SaveDraftAsync(draft with { Definition = Stamp(draft.Definition) }, cancellationToken);
    }

    public async Task<IReadOnlyList<DynamicEndpointDraft>> ListDraftsAsync(CancellationToken cancellationToken = default) =>
        (await inner.ListDraftsAsync(cancellationToken)).Where(d => Owns(d.Definition)).ToList();

    public async Task<DynamicEndpointDraft?> GetDraftAsync(Guid id, CancellationToken cancellationToken = default) =>
        await inner.GetDraftAsync(id, cancellationToken) is { } draft && Owns(draft.Definition) ? draft : null;

    public async Task<bool> DiscardDraftAsync(Guid id, CancellationToken cancellationToken = default) =>
        await GetDraftAsync(id, cancellationToken) is not null && await inner.DiscardDraftAsync(id, cancellationToken);

    public async Task<DynamicEndpointDefinition> PublishAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await EnsureOwnedAsync(id, cancellationToken);
        if (await GetDraftAsync(id, cancellationToken) is null)
        {
            throw new DynamicEndpointNotFoundException(id, $"Dynamic endpoint '{id}' has no draft.");
        }

        return await inner.PublishAsync(id, cancellationToken);
    }

    // Only the tenant's own due drafts; the scheduler of the unscoped manager publishes everything else.
    public async Task<IReadOnlyList<DynamicEndpointDefinition>> PublishDueAsync(CancellationToken cancellationToken = default)
    {
        var now = inner is DynamicEndpointManager manager ? manager.Now : DateTimeOffset.UtcNow;
        var published = new List<DynamicEndpointDefinition>();
        foreach (var draft in await ListDraftsAsync(cancellationToken))
        {
            if (draft.PublishAt is { } at && at <= now)
            {
                published.Add(await inner.PublishAsync(draft.EndpointId, cancellationToken));
            }
        }

        return published;
    }

    public async Task<IReadOnlyList<DynamicEndpointRevision>> GetHistoryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await EnsureOwnedAsync(id, cancellationToken);
        return await inner.GetHistoryAsync(id, cancellationToken);
    }

    public async Task<DynamicEndpointRevision?> GetRevisionAsync(Guid id, int revision, CancellationToken cancellationToken = default) =>
        await GetAsync(id, cancellationToken) is null ? null : await inner.GetRevisionAsync(id, revision, cancellationToken);

    public async Task<DynamicEndpointDefinition> RollbackAsync(Guid id, int revision, CancellationToken cancellationToken = default)
    {
        await EnsureOwnedAsync(id, cancellationToken);

        // The old content must not move the endpoint to another tenant (the unscoped manager may have reassigned it since).
        if (await inner.GetRevisionAsync(id, revision, cancellationToken) is { } target && !Owns(target.Definition))
        {
            throw new DynamicEndpointValidationException("tenant", $"Revision {revision} belongs to another tenant.");
        }

        return await inner.RollbackAsync(id, revision, cancellationToken);
    }

    public async Task<IReadOnlyList<DynamicEndpointDifference>> DiffAsync(Guid id, int fromRevision, int toRevision, CancellationToken cancellationToken = default)
    {
        await EnsureOwnedAsync(id, cancellationToken);
        return await inner.DiffAsync(id, fromRevision, toRevision, cancellationToken);
    }

    public async Task<IReadOnlyList<DynamicEndpointDifference>> DiffDraftAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await EnsureOwnedAsync(id, cancellationToken);
        return await inner.DiffDraftAsync(id, cancellationToken);
    }

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

    private bool Owns(DynamicEndpointState state) => Owns(state.Definition) && (state.Draft is null || Owns(state.Draft.Definition));

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

internal class TenantDynamicEndpointStore(IDynamicEndpointStore inner, string? tenant) : IDynamicEndpointStore
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

    protected bool Owns(DynamicEndpointDefinition definition) => DynamicEndpointsTenancyOptions.SameTenant(definition.Tenant, tenant);

    protected void Check(DynamicEndpointDefinition definition)
    {
        if (!Owns(definition))
        {
            throw new DynamicEndpointValidationException("tenant", tenant is null
                ? "The endpoint must be a shared endpoint (no tenant)."
                : $"The endpoint must belong to tenant '{tenant}'.");
        }
    }
}

// The tenant's view of a store that keeps history and drafts: revisions of the tenant's endpoints and its own drafts only.
internal sealed class TenantDynamicEndpointRevisionStore(IDynamicEndpointStore inner, string? tenant)
    : TenantDynamicEndpointStore(inner, tenant), IDynamicEndpointRevisionStore
{
    private readonly IDynamicEndpointStore _inner = inner;
    private readonly IDynamicEndpointRevisionStore _revisions = (IDynamicEndpointRevisionStore)inner;

    public Task AddRevisionAsync(DynamicEndpointRevision revision, CancellationToken cancellationToken)
    {
        Check(revision.Definition);
        return _revisions.AddRevisionAsync(revision, cancellationToken);
    }

    public async Task<IReadOnlyList<DynamicEndpointRevision>> GetRevisionsAsync(Guid endpointId, CancellationToken cancellationToken) =>
        await IsVisibleAsync(endpointId, cancellationToken) ? await _revisions.GetRevisionsAsync(endpointId, cancellationToken) : [];

    public async Task<DynamicEndpointRevision?> FindRevisionAsync(Guid endpointId, int revision, CancellationToken cancellationToken) =>
        await IsVisibleAsync(endpointId, cancellationToken) ? await _revisions.FindRevisionAsync(endpointId, revision, cancellationToken) : null;

    public async Task<IReadOnlyList<DynamicEndpointDraft>> GetDraftsAsync(CancellationToken cancellationToken) =>
        (await _revisions.GetDraftsAsync(cancellationToken)).Where(d => Owns(d.Definition)).ToList();

    public async Task<DynamicEndpointDraft?> FindDraftAsync(Guid endpointId, CancellationToken cancellationToken) =>
        await _revisions.FindDraftAsync(endpointId, cancellationToken) is { } draft && Owns(draft.Definition) ? draft : null;

    public async Task SaveDraftAsync(DynamicEndpointDraft draft, CancellationToken cancellationToken)
    {
        Check(draft.Definition);
        if (await _inner.FindAsync(draft.EndpointId, cancellationToken) is { } existing && !Owns(existing)
            || await _revisions.FindDraftAsync(draft.EndpointId, cancellationToken) is { } other && !Owns(other.Definition))
        {
            throw new DynamicEndpointValidationException("id", $"An endpoint with id '{draft.EndpointId}' already exists.");
        }

        await _revisions.SaveDraftAsync(draft, cancellationToken);
    }

    public async Task<bool> DeleteDraftAsync(Guid endpointId, CancellationToken cancellationToken) =>
        await FindDraftAsync(endpointId, cancellationToken) is not null && await _revisions.DeleteDraftAsync(endpointId, cancellationToken);

    // The endpoint is the tenant's, or it only exists as one of the tenant's drafts.
    private async Task<bool> IsVisibleAsync(Guid endpointId, CancellationToken cancellationToken) =>
        await _inner.FindAsync(endpointId, cancellationToken) is { } definition
            ? Owns(definition)
            : await FindDraftAsync(endpointId, cancellationToken) is not null;
}
