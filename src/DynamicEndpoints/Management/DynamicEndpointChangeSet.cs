using System.Text.Json;
using DynamicEndpoints.Management;
using DynamicEndpoints.Runtime;
using Microsoft.Extensions.Logging;

namespace DynamicEndpoints;

/// <summary>
/// Changes written through a store of your choice – typically one bound to your own <c>DbContext</c> and transaction – and
/// applied to the routing table only when you call <see cref="ApplyAsync"/>, once your transaction committed.
/// Create it with <see cref="IDynamicEndpointManager.BeginChanges(IDynamicEndpointStore)"/>. Not thread-safe; use it for one unit of work.
/// </summary>
/// <example>
/// <code>
/// var changes = manager.BeginChanges(db.GetDynamicEndpointStore()); // EF Core: tracked by db, saved by you
/// await changes.UpsertAsync(definition, ct);
/// db.FeatureVersions.Add(version);
/// await db.SaveChangesAsync(ct);                                     // one SaveChanges – one transaction
/// await changes.ApplyAsync(ct);                                      // routing table, change handlers, other instances
/// </code>
/// </example>
public sealed class DynamicEndpointChangeSet
{
    private readonly DynamicEndpointManager _manager;
    private readonly IDynamicEndpointStore _store;
    private readonly List<StagedChange> _changes = [];
    private readonly Func<DynamicEndpointDefinition, DynamicEndpointDefinition>? _prepare;

    // State of this unit of work, read before the store: what the definition looks like once the changes are saved.
    private readonly Dictionary<Guid, DynamicEndpointDefinition?> _definitions = [];
    private readonly Dictionary<Guid, CompiledEndpoint?> _compiled = [];
    private bool _applied;

    internal DynamicEndpointChangeSet(
        DynamicEndpointManager manager,
        IDynamicEndpointStore store,
        Func<DynamicEndpointDefinition, DynamicEndpointDefinition>? prepare = null)
    {
        _manager = manager;
        _store = store;
        _prepare = prepare;
    }

    /// <summary>Changes staged so far (with <see cref="DynamicEndpointChangeOrigin.Local"/>).</summary>
    public IReadOnlyList<DynamicEndpointChangedEvent> Changes => _changes.Select(c => c.Event).ToList();

    public bool IsApplied => _applied;

    /// <exception cref="DynamicEndpointValidationException">The definition is invalid, clashes with another route or the id is taken.</exception>
    public Task<DynamicEndpointDefinition> CreateAsync(DynamicEndpointDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition = Prepare(definition);
        return LockedAsync(async () =>
        {
            var id = definition.Id == Guid.Empty ? Guid.CreateVersion7() : definition.Id;
            if (await FindAsync(id, cancellationToken) is not null)
            {
                throw new DynamicEndpointValidationException("id", $"An endpoint with id '{id}' already exists.");
            }

            return await CreateCoreAsync(definition with { Id = id }, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>Replaces a definition; its <see cref="DynamicEndpointDefinition.Revision"/> must match the stored one.</summary>
    /// <exception cref="DynamicEndpointValidationException" />
    /// <exception cref="DynamicEndpointNotFoundException" />
    /// <exception cref="DynamicEndpointConcurrencyException" />
    public Task<DynamicEndpointDefinition> UpdateAsync(DynamicEndpointDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition = Prepare(definition);
        return LockedAsync(async () =>
        {
            var current = await FindAsync(definition.Id, cancellationToken) ?? throw new DynamicEndpointNotFoundException(definition.Id);
            if (current.Revision != definition.Revision)
            {
                throw new DynamicEndpointConcurrencyException(definition.Id, definition.Revision, current.Revision);
            }

            return await UpdateCoreAsync(definition, current, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    /// Creates the definition, or replaces the stored one with the same <see cref="DynamicEndpointDefinition.Id"/> regardless of its
    /// revision (last writer wins). Nothing is written when the stored definition is already the same – the revision stays.
    /// </summary>
    /// <exception cref="DynamicEndpointValidationException" />
    public Task<DynamicEndpointDefinition> UpsertAsync(DynamicEndpointDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition = Prepare(definition);
        return LockedAsync(async () =>
        {
            var current = definition.Id == Guid.Empty ? null : await FindAsync(definition.Id, cancellationToken);
            if (current is null)
            {
                return await CreateCoreAsync(definition.Id == Guid.Empty ? definition with { Id = Guid.CreateVersion7() } : definition, cancellationToken);
            }

            var normalized = DefinitionNormalizer.Normalize(definition);
            if (SameContent(normalized, current))
            {
                return DynamicEndpointsJson.DeepClone(current);
            }

            return await UpdateCoreAsync(definition, current, cancellationToken);
        }, cancellationToken);
    }

    public Task<DynamicEndpointDefinition> SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default) =>
        LockedAsync(async () =>
        {
            var current = await FindAsync(id, cancellationToken) ?? throw new DynamicEndpointNotFoundException(id);
            return current.Enabled == enabled
                ? DynamicEndpointsJson.DeepClone(current)
                : await UpdateCoreAsync(current with { Enabled = enabled }, current, cancellationToken);
        }, cancellationToken);

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        LockedAsync(async () =>
        {
            var current = await FindAsync(id, cancellationToken);
            if (!await _store.DeleteAsync(id, cancellationToken))
            {
                return false;
            }

            _definitions[id] = null;
            _compiled[id] = null;
            _changes.Add(new StagedChange(new DynamicEndpointChangedEvent(DynamicEndpointChangeKind.Deleted, id, DynamicEndpointChangeOrigin.Local, null, current), null));
            return true;
        }, cancellationToken);

    /// <summary>
    /// Applies the staged changes to the routing table of this instance, runs the change handlers and notifies the other instances.
    /// Call it after the changes were committed – before that, the routes would serve definitions that may still be rolled back.
    /// When you roll back instead, simply drop the change set.
    /// </summary>
    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        if (_applied)
        {
            throw new InvalidOperationException("The changes were already applied.");
        }

        _applied = true;
        if (_changes.Count == 0)
        {
            return;
        }

        await _manager.ApplyAsync(_changes, cancellationToken);
    }

    private async Task<DynamicEndpointDefinition> CreateCoreAsync(DynamicEndpointDefinition definition, CancellationToken cancellationToken)
    {
        var now = _manager.Now;
        var d = DefinitionNormalizer.Normalize(definition) with
        {
            Revision = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var compiled = await CompileOrThrowAsync(d, cancellationToken);
        await _store.AddAsync(d, cancellationToken);
        Stage(DynamicEndpointChangeKind.Created, d, null, compiled);
        _manager.Logger.LogInformation("Created dynamic endpoint {Method} {Route} ({Id}).", d.Method, d.Route, d.Id);
        return DynamicEndpointsJson.DeepClone(d);
    }

    private async Task<DynamicEndpointDefinition> UpdateCoreAsync(DynamicEndpointDefinition definition, DynamicEndpointDefinition current, CancellationToken cancellationToken)
    {
        var d = DefinitionNormalizer.Normalize(definition) with
        {
            Revision = current.Revision + 1,
            CreatedAt = current.CreatedAt,
            UpdatedAt = _manager.Now,
        };

        var compiled = await CompileOrThrowAsync(d, cancellationToken);
        await _store.UpdateAsync(d, current.Revision, cancellationToken);
        Stage(DynamicEndpointChangeKind.Updated, d, current, compiled);
        _manager.Logger.LogInformation("Updated dynamic endpoint {Method} {Route} ({Id}) to revision {Revision}.", d.Method, d.Route, d.Id, d.Revision);
        return DynamicEndpointsJson.DeepClone(d);
    }

    private void Stage(DynamicEndpointChangeKind kind, DynamicEndpointDefinition d, DynamicEndpointDefinition? previous, CompiledEndpoint compiled)
    {
        _definitions[d.Id] = d;
        _compiled[d.Id] = compiled;

        // Created and then changed in the same unit of work is still a creation for everybody else.
        var created = _changes.FindIndex(c => c.Event.Id == d.Id && c.Event.Kind == DynamicEndpointChangeKind.Created);
        _changes.Add(created >= 0 && kind == DynamicEndpointChangeKind.Updated
            ? new StagedChange(new DynamicEndpointChangedEvent(DynamicEndpointChangeKind.Created, d.Id, DynamicEndpointChangeOrigin.Local, d, null), compiled)
            : new StagedChange(new DynamicEndpointChangedEvent(kind, d.Id, DynamicEndpointChangeOrigin.Local, d, previous), compiled));
        if (created >= 0)
        {
            _changes.RemoveAt(created);
        }
    }

    // E.g. assigns the tenant of a tenant-scoped manager.
    private DynamicEndpointDefinition Prepare(DynamicEndpointDefinition definition) => _prepare?.Invoke(definition) ?? definition;

    private async Task<DynamicEndpointDefinition?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        _definitions.TryGetValue(id, out var staged) ? staged : await _store.FindAsync(id, cancellationToken);

    private async Task<CompiledEndpoint> CompileOrThrowAsync(DynamicEndpointDefinition d, CancellationToken cancellationToken)
    {
        // Routes of this unit of work count as already active, so two staged endpoints can't take the same route either.
        var active = _manager.Runtime.ActiveEndpoints()
            .Where(e => !_compiled.ContainsKey(e.Definition.Id))
            .Concat(_compiled.Values.Where(c => c is not null && c.Definition.Enabled)!);
        var result = await _manager.CompileAsync(d, active!, cancellationToken);
        return result.Endpoint ?? throw new DynamicEndpointValidationException(result.Errors.ToDictionary());
    }

    private async Task<T> LockedAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        if (_applied)
        {
            throw new InvalidOperationException("The changes were already applied – begin a new change set.");
        }

        await _manager.Gate.WaitAsync(cancellationToken);
        try
        {
            return await action();
        }
        finally
        {
            _manager.Gate.Release();
        }
    }

    private static bool SameContent(DynamicEndpointDefinition candidate, DynamicEndpointDefinition current)
    {
        static string Content(DynamicEndpointDefinition d) => JsonSerializer.Serialize(
            d with { Revision = 0, CreatedAt = default, UpdatedAt = default },
            DynamicEndpointsJson.SerializerOptions);

        return Content(candidate) == Content(DefinitionNormalizer.Normalize(current));
    }
}

internal sealed record StagedChange(DynamicEndpointChangedEvent Event, CompiledEndpoint? Compiled);
