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

    /// <summary>A rehearsal (dry run, import validation) – writes go to a throwaway store, so nothing is logged as done.</summary>
    internal bool IsRehearsal { get; set; }
    private readonly IDynamicEndpointStore _store;
    private readonly IDynamicEndpointRevisionStore? _revisions;
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
        _revisions = store as IDynamicEndpointRevisionStore;
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

            return await CreateCoreAsync(definition with { Id = id }, DynamicEndpointRevisionKind.Created, null, cancellationToken);
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

            return await UpdateCoreAsync(definition, current, new(DynamicEndpointRevisionKind.Updated), cancellationToken);
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
                return await CreateCoreAsync(definition.Id == Guid.Empty ? definition with { Id = Guid.CreateVersion7() } : definition,
                    DynamicEndpointRevisionKind.Created, null, cancellationToken);
            }

            var normalized = DefinitionNormalizer.Normalize(definition);
            if (SameContent(normalized, current))
            {
                return DynamicEndpointsJson.DeepClone(current);
            }

            return await UpdateCoreAsync(definition, current, new(DynamicEndpointRevisionKind.Updated), cancellationToken);
        }, cancellationToken);
    }

    public Task<DynamicEndpointDefinition> SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default) =>
        LockedAsync(async () =>
        {
            var current = await FindAsync(id, cancellationToken) ?? throw new DynamicEndpointNotFoundException(id);
            return current.Enabled == enabled
                ? DynamicEndpointsJson.DeepClone(current)
                : await UpdateCoreAsync(current with { Enabled = enabled }, current,
                    new(enabled ? DynamicEndpointRevisionKind.Enabled : DynamicEndpointRevisionKind.Disabled), cancellationToken);
        }, cancellationToken);

    /// <summary>Deletes the endpoint together with its history and draft – or only the draft of a never published endpoint.</summary>
    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        LockedAsync(async () =>
        {
            var current = await FindAsync(id, cancellationToken);
            if (!await _store.DeleteAsync(id, cancellationToken))
            {
                return _revisions is not null && await _revisions.DeleteDraftAsync(id, cancellationToken);
            }

            _definitions[id] = null;
            _compiled[id] = null;
            _changes.Add(new StagedChange(new DynamicEndpointChangedEvent(DynamicEndpointChangeKind.Deleted, id, DynamicEndpointChangeOrigin.Local, null, current), null));
            return true;
        }, cancellationToken);

    /// <summary>
    /// Saves a draft: validated like a published definition, but not routed until <see cref="PublishAsync"/> (or its
    /// <see cref="DynamicEndpointDraft.PublishAt"/>). Without an id, a new endpoint is drafted. Replaces an existing draft.
    /// </summary>
    /// <exception cref="DynamicEndpointValidationException" />
    /// <exception cref="DynamicEndpointNotFoundException">The draft is based on a revision of an endpoint that doesn't exist.</exception>
    /// <exception cref="DynamicEndpointConcurrencyException">The draft is based on a revision newer than the published one.</exception>
    /// <exception cref="NotSupportedException">The store keeps no drafts (<see cref="IDynamicEndpointRevisionStore"/>).</exception>
    public Task<DynamicEndpointDraft> SaveDraftAsync(DynamicEndpointDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(draft.Definition);
        var revisions = RequireRevisions();
        draft = draft with { Definition = Prepare(draft.Definition) };
        return LockedAsync(async () =>
        {
            var id = draft.Definition.Id == Guid.Empty ? Guid.CreateVersion7() : draft.Definition.Id;
            var current = await FindAsync(id, cancellationToken);
            var baseRevision = draft.Definition.Revision == 0 ? current?.Revision ?? 0 : draft.Definition.Revision;
            if (current is null && baseRevision != 0)
            {
                throw new DynamicEndpointNotFoundException(id);
            }

            if (current is not null && baseRevision > current.Revision)
            {
                throw new DynamicEndpointConcurrencyException(id, baseRevision, current.Revision);
            }

            var d = DefinitionNormalizer.Normalize(draft.Definition) with
            {
                Id = id,
                Revision = baseRevision,
                CreatedAt = current?.CreatedAt ?? default,
                UpdatedAt = current?.UpdatedAt ?? default,
            };
            await CompileOrThrowAsync(d, cancellationToken);

            var now = _manager.Now;
            var existing = await revisions.FindDraftAsync(id, cancellationToken);
            var saved = new DynamicEndpointDraft
            {
                Definition = d,
                PublishAt = draft.PublishAt?.ToUniversalTime(), // some providers (Npgsql) only store UTC offsets
                Comment = string.IsNullOrWhiteSpace(draft.Comment) ? null : draft.Comment.Trim(),
                CreatedAt = existing?.CreatedAt ?? now,
                UpdatedAt = now,
            };
            await revisions.SaveDraftAsync(saved, cancellationToken);
            _manager.Logger.LogInformation("Saved draft of dynamic endpoint {Method} {Route} ({Id}) based on revision {Revision}.",
                d.Method, d.Route, d.Id, baseRevision);
            return DynamicEndpointsJson.DeepClone(saved);
        }, cancellationToken);
    }

    /// <summary>Drops the draft of an endpoint; the published revision stays as it is.</summary>
    /// <exception cref="NotSupportedException">The store keeps no drafts.</exception>
    public Task<bool> DiscardDraftAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var revisions = RequireRevisions();
        return LockedAsync(() => revisions.DeleteDraftAsync(id, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Publishes the draft of an endpoint: it becomes the next revision (or the first one of a new endpoint), and the draft is gone.
    /// </summary>
    /// <exception cref="DynamicEndpointNotFoundException">There is no draft.</exception>
    /// <exception cref="DynamicEndpointConcurrencyException">The endpoint changed since the draft was based on it.</exception>
    /// <exception cref="DynamicEndpointValidationException">The draft is no longer valid, e.g. its route is taken now.</exception>
    /// <exception cref="NotSupportedException">The store keeps no drafts.</exception>
    public Task<DynamicEndpointDefinition> PublishAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var revisions = RequireRevisions();
        return LockedAsync(async () =>
        {
            var draft = await revisions.FindDraftAsync(id, cancellationToken)
                ?? throw new DynamicEndpointNotFoundException(id, $"Dynamic endpoint '{id}' has no draft.");
            var current = await FindAsync(id, cancellationToken);
            if ((current?.Revision ?? 0) != draft.BaseRevision)
            {
                throw new DynamicEndpointConcurrencyException(id, draft.BaseRevision, current?.Revision);
            }

            return current is null
                ? await CreateCoreAsync(draft.Definition, DynamicEndpointRevisionKind.Published, draft.Comment, cancellationToken)
                : await UpdateCoreAsync(draft.Definition, current, new(DynamicEndpointRevisionKind.Published, draft.Comment), cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    /// Restores the content of an earlier revision as the next revision, so the history stays complete. A draft is left alone.
    /// </summary>
    /// <exception cref="DynamicEndpointNotFoundException">The endpoint or the revision doesn't exist.</exception>
    /// <exception cref="DynamicEndpointValidationException">The old revision is no longer valid, e.g. its processor was removed.</exception>
    /// <exception cref="NotSupportedException">The store keeps no history.</exception>
    public Task<DynamicEndpointDefinition> RollbackAsync(Guid id, int revision, CancellationToken cancellationToken = default)
    {
        var revisions = RequireRevisions();
        return LockedAsync(async () =>
        {
            var current = await FindAsync(id, cancellationToken) ?? throw new DynamicEndpointNotFoundException(id);
            if (revision == current.Revision)
            {
                return DynamicEndpointsJson.DeepClone(current);
            }

            var target = await revisions.FindRevisionAsync(id, revision, cancellationToken)
                ?? throw new DynamicEndpointNotFoundException(id, $"Dynamic endpoint '{id}' has no revision {revision}.");
            return await UpdateCoreAsync(target.Definition, current,
                new(DynamicEndpointRevisionKind.RolledBack, $"Rolled back to revision {revision}.", revision), cancellationToken);
        }, cancellationToken);
    }

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

    private async Task<DynamicEndpointDefinition> CreateCoreAsync(
        DynamicEndpointDefinition definition, DynamicEndpointRevisionKind kind, string? comment, CancellationToken cancellationToken)
    {
        var now = _manager.Now;
        var d = DefinitionNormalizer.Normalize(definition) with
        {
            Revision = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var compiled = await CompileOrThrowAsync(d, cancellationToken);
        if (_revisions is not null)
        {
            await _revisions.AddRevisionAsync(new DynamicEndpointRevision { Definition = d, Kind = kind, Comment = comment }, cancellationToken);
        }

        await _store.AddAsync(d, cancellationToken);
        Stage(DynamicEndpointChangeKind.Created, d, null, compiled);
        if (!IsRehearsal)
        {
            _manager.Logger.LogInformation("Created dynamic endpoint {Method} {Route} ({Id}).", d.Method, d.Route, d.Id);
        }

        return DynamicEndpointsJson.DeepClone(d);
    }

    private async Task<DynamicEndpointDefinition> UpdateCoreAsync(
        DynamicEndpointDefinition definition, DynamicEndpointDefinition current, RevisionInfo info, CancellationToken cancellationToken)
    {
        var d = DefinitionNormalizer.Normalize(definition) with
        {
            Revision = current.Revision + 1,
            CreatedAt = current.CreatedAt,
            UpdatedAt = _manager.Now,
        };

        var compiled = await CompileOrThrowAsync(d, cancellationToken);
        if (_revisions is not null)
        {
            // Definitions saved before the history was kept get their current revision recorded first.
            if (!_definitions.ContainsKey(d.Id) && await _revisions.FindRevisionAsync(d.Id, current.Revision, cancellationToken) is null)
            {
                await _revisions.AddRevisionAsync(Baseline(current), cancellationToken);
            }

            await _revisions.AddRevisionAsync(new DynamicEndpointRevision
            {
                Definition = d,
                Kind = info.Kind,
                Comment = info.Comment,
                SourceRevision = info.SourceRevision,
            }, cancellationToken);
        }

        await _store.UpdateAsync(d, current.Revision, cancellationToken);
        Stage(DynamicEndpointChangeKind.Updated, d, current, compiled);
        if (!IsRehearsal)
        {
            _manager.Logger.LogInformation("Updated dynamic endpoint {Method} {Route} ({Id}) to revision {Revision}.", d.Method, d.Route, d.Id, d.Revision);
        }

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

    /// <summary>A revision for a definition the history doesn't know – what happened to it can only be guessed.</summary>
    internal static DynamicEndpointRevision Baseline(DynamicEndpointDefinition definition) => new()
    {
        Definition = DynamicEndpointsJson.DeepClone(definition),
        Kind = definition.Revision <= 1 ? DynamicEndpointRevisionKind.Created : DynamicEndpointRevisionKind.Updated,
    };

    internal static NotSupportedException RevisionsNotSupported() => new(
        $"The dynamic endpoint store keeps no history and drafts – it doesn't implement {nameof(IDynamicEndpointRevisionStore)}.");

    private IDynamicEndpointRevisionStore RequireRevisions() => _revisions ?? throw RevisionsNotSupported();
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

internal readonly record struct RevisionInfo(DynamicEndpointRevisionKind Kind, string? Comment = null, int? SourceRevision = null);
