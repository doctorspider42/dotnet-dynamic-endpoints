using DynamicEndpoints.Processing;
using DynamicEndpoints.Runtime;
using DynamicEndpoints.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DynamicEndpoints.Management;

internal sealed class DynamicEndpointManager(
    IServiceScopeFactory scopeFactory,
    DynamicEndpointCompiler compiler,
    DynamicEndpointRuntime runtime,
    ProcessorRegistry processors,
    ValidatorRegistry validators,
    TimeProvider timeProvider,
    IOptions<DynamicEndpointsOptions> options,
    ILogger<DynamicEndpointManager> logger,
    IDynamicEndpointChangeNotifier? notifier = null) : IDynamicEndpointManager, IDisposable
{
    // Serializes mutations and reloads of this instance, so a reload can never resurrect stale state.
    internal SemaphoreSlim Gate { get; } = new(1, 1);

    internal DynamicEndpointRuntime Runtime => runtime;

    internal ILogger Logger => logger;

    internal DateTimeOffset Now => timeProvider.GetUtcNow();

    private bool _loaded;

    public IReadOnlyList<DynamicProcessorDescriptor> Processors => processors.All;

    public IReadOnlyList<DynamicValidatorDescriptor> Validators => validators.All;

    public async Task<IReadOnlyList<DynamicEndpointState>> ListAsync(CancellationToken cancellationToken = default)
    {
        var (definitions, drafts) = await WithStoreAsync(async store =>
        {
            var definitions = await store.GetAllAsync(cancellationToken);
            var drafts = store is IDynamicEndpointRevisionStore revisions ? await revisions.GetDraftsAsync(cancellationToken) : [];
            return (definitions, drafts.ToDictionary(d => d.EndpointId));
        });

        return definitions
            .Select(d => ToState(d) with { Draft = drafts.Remove(d.Id, out var draft) ? draft : null })
            .Concat(drafts.Values.Select(DraftState))
            .OrderBy(s => s.Definition.Route, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Definition.Method, StringComparer.Ordinal)
            .ToList();
    }

    public Task<DynamicEndpointState?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        WithStoreAsync(async store =>
        {
            var definition = await store.FindAsync(id, cancellationToken);
            var draft = store is IDynamicEndpointRevisionStore revisions ? await revisions.FindDraftAsync(id, cancellationToken) : null;
            return definition is not null ? ToState(definition) with { Draft = draft }
                : draft is not null ? DraftState(draft)
                : null;
        });

    public Task<DynamicEndpointDefinition> CreateAsync(DynamicEndpointDefinition definition, CancellationToken cancellationToken = default) =>
        WithChangesAsync(changes => changes.CreateAsync(definition, cancellationToken), cancellationToken);

    public Task<DynamicEndpointDefinition> UpdateAsync(DynamicEndpointDefinition definition, CancellationToken cancellationToken = default) =>
        WithChangesAsync(changes => changes.UpdateAsync(definition, cancellationToken), cancellationToken);

    public async Task<DynamicEndpointDefinition> UpsertAsync(DynamicEndpointDefinition definition, CancellationToken cancellationToken = default)
    {
        // Another instance may write between our read and write – the store's concurrency check catches it, a retry resolves it.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await WithChangesAsync(changes => changes.UpsertAsync(definition, cancellationToken), cancellationToken);
            }
            catch (DynamicEndpointConcurrencyException) when (attempt < 3)
            {
            }
        }
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        WithChangesAsync(changes => changes.DeleteAsync(id, cancellationToken), cancellationToken);

    public Task<DynamicEndpointDefinition> SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default) =>
        WithChangesAsync(changes => changes.SetEnabledAsync(id, enabled, cancellationToken), cancellationToken);

    public Task<DynamicEndpointDraft> SaveDraftAsync(DynamicEndpointDraft draft, CancellationToken cancellationToken = default) =>
        WithChangesAsync(changes => changes.SaveDraftAsync(draft, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<DynamicEndpointDraft>> ListDraftsAsync(CancellationToken cancellationToken = default) =>
        WithRevisionsAsync((_, revisions) => revisions.GetDraftsAsync(cancellationToken));

    public Task<DynamicEndpointDraft?> GetDraftAsync(Guid id, CancellationToken cancellationToken = default) =>
        WithRevisionsAsync((_, revisions) => revisions.FindDraftAsync(id, cancellationToken));

    public Task<bool> DiscardDraftAsync(Guid id, CancellationToken cancellationToken = default) =>
        WithChangesAsync(changes => changes.DiscardDraftAsync(id, cancellationToken), cancellationToken);

    public Task<DynamicEndpointDefinition> PublishAsync(Guid id, CancellationToken cancellationToken = default) =>
        WithChangesAsync(changes => changes.PublishAsync(id, cancellationToken), cancellationToken);

    public async Task<IReadOnlyList<DynamicEndpointDefinition>> PublishDueAsync(CancellationToken cancellationToken = default)
    {
        var now = Now;
        var due = await WithStoreAsync(async store => store is IDynamicEndpointRevisionStore revisions
            ? (await revisions.GetDraftsAsync(cancellationToken)).Where(d => d.PublishAt <= now).OrderBy(d => d.PublishAt).ToList()
            : []);

        var published = new List<DynamicEndpointDefinition>();
        foreach (var draft in due)
        {
            try
            {
                published.Add(await PublishAsync(draft.EndpointId, cancellationToken));
                logger.LogInformation("Published the draft of dynamic endpoint {Method} {Route} ({Id}) scheduled for {PublishAt}.",
                    draft.Definition.Method, draft.Definition.Route, draft.EndpointId, draft.PublishAt);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (ex is DynamicEndpointException && await GetDraftAsync(draft.EndpointId, cancellationToken) is null)
                {
                    // Another instance was faster.
                    logger.LogDebug(ex, "The scheduled draft of dynamic endpoint {Id} was already published.", draft.EndpointId);
                    continue;
                }

                logger.LogWarning(ex, "The draft of dynamic endpoint {Method} {Route} ({Id}) scheduled for {PublishAt} could not be published.",
                    draft.Definition.Method, draft.Definition.Route, draft.EndpointId, draft.PublishAt);
                if (ex is DynamicEndpointException)
                {
                    // Invalid or based on an outdated revision – that won't fix itself. Keep the draft, but stop trying.
                    await WithStoreAsync(async store =>
                    {
                        await ((IDynamicEndpointRevisionStore)store).SaveDraftAsync(draft with { PublishAt = null }, cancellationToken);
                        return true;
                    });
                }
            }
        }

        return published;
    }

    public Task<IReadOnlyList<DynamicEndpointRevision>> GetHistoryAsync(Guid id, CancellationToken cancellationToken = default) =>
        WithRevisionsAsync<IReadOnlyList<DynamicEndpointRevision>>(async (store, revisions) =>
        {
            var history = await revisions.GetRevisionsAsync(id, cancellationToken);
            var current = await store.FindAsync(id, cancellationToken);

            // Saved before the history was kept – the published revision is part of the history all the same.
            return current is not null && !history.Any(r => r.Revision == current.Revision)
                ? [DynamicEndpointChangeSet.Baseline(current), .. history.Where(r => r.Revision < current.Revision)]
                : history;
        });

    public Task<DynamicEndpointRevision?> GetRevisionAsync(Guid id, int revision, CancellationToken cancellationToken = default) =>
        WithRevisionsAsync((store, revisions) => FindRevisionAsync(store, revisions, id, revision, cancellationToken));

    public Task<DynamicEndpointDefinition> RollbackAsync(Guid id, int revision, CancellationToken cancellationToken = default) =>
        WithChangesAsync(changes => changes.RollbackAsync(id, revision, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<DynamicEndpointDifference>> DiffAsync(Guid id, int fromRevision, int toRevision, CancellationToken cancellationToken = default) =>
        WithRevisionsAsync(async (store, revisions) =>
        {
            var from = await FindRevisionAsync(store, revisions, id, fromRevision, cancellationToken) ?? throw NoRevision(id, fromRevision);
            var to = await FindRevisionAsync(store, revisions, id, toRevision, cancellationToken) ?? throw NoRevision(id, toRevision);
            return DynamicEndpointDiff.Compare(from.Definition, to.Definition);
        });

    public Task<IReadOnlyList<DynamicEndpointDifference>> DiffDraftAsync(Guid id, CancellationToken cancellationToken = default) =>
        WithRevisionsAsync(async (store, revisions) =>
        {
            var draft = await revisions.FindDraftAsync(id, cancellationToken)
                ?? throw new DynamicEndpointNotFoundException(id, $"Dynamic endpoint '{id}' has no draft.");
            return DynamicEndpointDiff.Compare(await store.FindAsync(id, cancellationToken), draft.Definition);
        });

    public DynamicEndpointChangeSet BeginChanges(IDynamicEndpointStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return new DynamicEndpointChangeSet(this, store);
    }

    internal DynamicEndpointChangeSet BeginChanges(IDynamicEndpointStore store, Func<DynamicEndpointDefinition, DynamicEndpointDefinition> prepare) =>
        new(this, store, prepare);

    public DynamicEndpointChangeSet BeginChanges(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return BeginChanges(services.GetRequiredService<IDynamicEndpointStore>());
    }

    public async Task<DynamicEndpointValidationResult> ValidateAsync(DynamicEndpointDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var result = await CompileAsync(DefinitionNormalizer.Normalize(definition), runtime.ActiveEndpoints(), cancellationToken);
        return new DynamicEndpointValidationResult(!result.Errors.HasErrors, result.Errors.ToDictionary());
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        List<DynamicEndpointChangedEvent> changes;
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var definitions = await WithStoreAsync(store => store.GetAllAsync(cancellationToken));
            var previous = runtime.Entries.ToDictionary(e => e.Definition.Id);
            var entries = new List<RuntimeEntry>(definitions.Count);
            var active = new List<CompiledEndpoint>();
            changes = [];

            // Oldest wins when the store contains clashing routes (e.g. edited by hand).
            foreach (var definition in definitions.OrderBy(d => d.CreatedAt).ThenBy(d => d.Id))
            {
                var existing = previous.GetValueOrDefault(definition.Id);
                var unchanged = existing is not null &&
                    existing.Definition.Revision == definition.Revision &&
                    existing.Definition.UpdatedAt == definition.UpdatedAt;

                CompiledEndpoint? compiled;
                List<string> errors;
                if (unchanged && existing!.Compiled is not null)
                {
                    compiled = existing.Compiled;
                    errors = [];
                }
                else
                {
                    var result = await compiler.CompileAsync(DefinitionNormalizer.Normalize(definition), cancellationToken);
                    compiled = result.Endpoint;
                    errors = result.Errors.Flatten().ToList();
                }

                if (compiled is not null && definition.Enabled)
                {
                    errors.AddRange(runtime.FindConflicts(compiled, active));
                    if (errors.Count == 0)
                    {
                        active.Add(compiled);
                    }
                }

                if (errors.Count > 0)
                {
                    logger.LogWarning("Dynamic endpoint {Method} {Route} ({Id}) could not be activated: {Errors}",
                        definition.Method, definition.Route, definition.Id, string.Join(" | ", errors));
                }

                if (!unchanged)
                {
                    changes.Add(new DynamicEndpointChangedEvent(
                        existing is null ? DynamicEndpointChangeKind.Created : DynamicEndpointChangeKind.Updated,
                        definition.Id, DynamicEndpointChangeOrigin.Remote, definition, existing?.Definition));
                }

                entries.Add(new RuntimeEntry(definition, compiled, errors));
                previous.Remove(definition.Id);
            }

            changes.AddRange(previous.Values.Select(e => new DynamicEndpointChangedEvent(
                DynamicEndpointChangeKind.Deleted, e.Definition.Id, DynamicEndpointChangeOrigin.Remote, null, e.Definition)));

            runtime.Replace(entries);
            logger.LogDebug("Loaded {Count} dynamic endpoints ({Active} active).", entries.Count, active.Count);

            // The initial load is no change – everything simply appears.
            if (!_loaded)
            {
                _loaded = true;
                changes.Clear();
            }
        }
        finally
        {
            Gate.Release();
        }

        await RaiseAsync(changes, cancellationToken);
    }

    public void Dispose() => Gate.Dispose();

    /// <summary>Applies committed changes of a change set: routing table, change handlers, other instances.</summary>
    internal async Task ApplyAsync(IReadOnlyList<StagedChange> changes, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            runtime.Apply(entries =>
            {
                foreach (var change in changes)
                {
                    var id = change.Event.Id;
                    if (change.Event.Kind == DynamicEndpointChangeKind.Deleted)
                    {
                        entries.Remove(id);
                        continue;
                    }

                    // A reload may already have picked up an even newer revision (e.g. from another instance) – never go back.
                    var definition = change.Event.Definition!;
                    if (entries.GetValueOrDefault(id) is { } loaded && loaded.Definition.Revision > definition.Revision)
                    {
                        continue;
                    }

                    entries[id] = new RuntimeEntry(definition, change.Compiled, []);
                }
            });
        }
        finally
        {
            Gate.Release();
        }

        var events = changes.Select(c => c.Event).ToList();
        await RaiseAsync(events, cancellationToken);
        await NotifyAsync(events);
    }

    internal async Task<CompilationResult> CompileAsync(DynamicEndpointDefinition d, IEnumerable<CompiledEndpoint> active, CancellationToken cancellationToken)
    {
        var result = await compiler.CompileAsync(d, cancellationToken);
        if (result.Endpoint is not null && d.Enabled)
        {
            foreach (var conflict in runtime.FindConflicts(result.Endpoint, active))
            {
                result.Errors.Add("route", conflict);
            }
        }

        return result.Errors.HasErrors ? result with { Endpoint = null } : result;
    }

    private async Task<T> WithChangesAsync<T>(Func<DynamicEndpointChangeSet, Task<T>> action, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var changes = BeginChanges(scope.ServiceProvider.GetRequiredService<IDynamicEndpointStore>());
        var result = await action(changes);
        await changes.ApplyAsync(cancellationToken);
        return result;
    }

    private async Task RaiseAsync(IReadOnlyList<DynamicEndpointChangedEvent> changes, CancellationToken cancellationToken)
    {
        if (changes.Count == 0)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var handlers = scope.ServiceProvider.GetServices<IDynamicEndpointChangeHandler>().ToList();
        foreach (var change in changes)
        {
            foreach (var handler in handlers)
            {
                try
                {
                    await handler.OnChangedAsync(change, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Change handler {Handler} failed for {Kind} of dynamic endpoint {Id}.",
                        handler.GetType().Name, change.Kind, change.Id);
                }
            }
        }
    }

    private async Task NotifyAsync(IReadOnlyList<DynamicEndpointChangedEvent> changes)
    {
        if (notifier is null)
        {
            return;
        }

        try
        {
            // Not tied to the caller's cancellation – the change is committed, the other instances must hear about it.
            var notification = new DynamicEndpointChangeNotification(options.Value.InstanceId, changes.Select(c => c.Id).Distinct().ToList());
            await notifier.PublishAsync(notification, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Notifying other instances about dynamic endpoint changes failed; they pick them up on their next refresh.");
        }
    }

    private static DynamicEndpointState DraftState(DynamicEndpointDraft draft) =>
        new(draft.Definition, DynamicEndpointStatus.Draft, []) { Draft = draft };

    private static async Task<DynamicEndpointRevision?> FindRevisionAsync(
        IDynamicEndpointStore store, IDynamicEndpointRevisionStore revisions, Guid id, int revision, CancellationToken cancellationToken)
    {
        if (await revisions.FindRevisionAsync(id, revision, cancellationToken) is { } found)
        {
            return found;
        }

        return await store.FindAsync(id, cancellationToken) is { } current && current.Revision == revision
            ? DynamicEndpointChangeSet.Baseline(current)
            : null;
    }

    private static DynamicEndpointNotFoundException NoRevision(Guid id, int revision) =>
        new(id, $"Dynamic endpoint '{id}' has no revision {revision}.");

    private Task<T> WithRevisionsAsync<T>(Func<IDynamicEndpointStore, IDynamicEndpointRevisionStore, Task<T>> action) =>
        WithStoreAsync(store => store is IDynamicEndpointRevisionStore revisions
            ? action(store, revisions)
            : throw DynamicEndpointChangeSet.RevisionsNotSupported());

    private DynamicEndpointState ToState(DynamicEndpointDefinition definition)
    {
        var entry = runtime.Find(definition.Id);
        if (entry is null || entry.Definition.Revision != definition.Revision)
        {
            return new DynamicEndpointState(definition, DynamicEndpointStatus.Pending, []);
        }

        if (entry.Errors.Count > 0 || entry.Compiled is null)
        {
            return new DynamicEndpointState(definition, DynamicEndpointStatus.Invalid, entry.Errors);
        }

        return new DynamicEndpointState(
            definition,
            definition.Enabled ? DynamicEndpointStatus.Active : DynamicEndpointStatus.Disabled,
            []);
    }

    private async Task<T> WithStoreAsync<T>(Func<IDynamicEndpointStore, Task<T>> action)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<IDynamicEndpointStore>());
    }
}
