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
        var definitions = await WithStoreAsync(store => store.GetAllAsync(cancellationToken));
        return definitions
            .OrderBy(d => d.Route, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Method, StringComparer.Ordinal)
            .Select(ToState)
            .ToList();
    }

    public async Task<DynamicEndpointState?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var definition = await WithStoreAsync(store => store.FindAsync(id, cancellationToken));
        return definition is null ? null : ToState(definition);
    }

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

    public DynamicEndpointChangeSet BeginChanges(IDynamicEndpointStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return new DynamicEndpointChangeSet(this, store);
    }

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
