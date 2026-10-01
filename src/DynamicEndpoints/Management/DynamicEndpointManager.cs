using DynamicEndpoints.Processing;
using DynamicEndpoints.Runtime;
using DynamicEndpoints.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicEndpoints.Management;

internal sealed class DynamicEndpointManager(
    IServiceScopeFactory scopeFactory,
    DynamicEndpointCompiler compiler,
    DynamicEndpointRuntime runtime,
    ProcessorRegistry processors,
    ValidatorRegistry validators,
    TimeProvider timeProvider,
    ILogger<DynamicEndpointManager> logger) : IDynamicEndpointManager, IDisposable
{
    // Serializes mutations and reloads of this instance, so a reload can never resurrect stale state.
    private readonly SemaphoreSlim _gate = new(1, 1);

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

    public async Task<DynamicEndpointDefinition> CreateAsync(DynamicEndpointDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = timeProvider.GetUtcNow();
            var d = DefinitionNormalizer.Normalize(definition) with
            {
                Id = definition.Id == Guid.Empty ? Guid.CreateVersion7() : definition.Id,
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now,
            };

            await using var scope = scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IDynamicEndpointStore>();
            if (await store.FindAsync(d.Id, cancellationToken) is not null)
            {
                throw new DynamicEndpointValidationException("id", $"An endpoint with id '{d.Id}' already exists.");
            }

            var compiled = await CompileOrThrowAsync(d, cancellationToken);
            await store.AddAsync(d, cancellationToken);
            runtime.Upsert(new RuntimeEntry(d, compiled, []));

            logger.LogInformation("Created dynamic endpoint {Method} {Route} ({Id}).", d.Method, d.Route, d.Id);
            return DynamicEndpointsJson.DeepClone(d);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DynamicEndpointDefinition> UpdateAsync(DynamicEndpointDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IDynamicEndpointStore>();
            var current = await store.FindAsync(definition.Id, cancellationToken)
                ?? throw new DynamicEndpointNotFoundException(definition.Id);
            if (current.Version != definition.Version)
            {
                throw new DynamicEndpointConcurrencyException(definition.Id, definition.Version, current.Version);
            }

            var d = DefinitionNormalizer.Normalize(definition) with
            {
                Version = current.Version + 1,
                CreatedAt = current.CreatedAt,
                UpdatedAt = timeProvider.GetUtcNow(),
            };

            var compiled = await CompileOrThrowAsync(d, cancellationToken);
            await store.UpdateAsync(d, current.Version, cancellationToken);
            runtime.Upsert(new RuntimeEntry(d, compiled, []));

            logger.LogInformation("Updated dynamic endpoint {Method} {Route} ({Id}) to version {Version}.", d.Method, d.Route, d.Id, d.Version);
            return DynamicEndpointsJson.DeepClone(d);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var deleted = await WithStoreAsync(store => store.DeleteAsync(id, cancellationToken));
            runtime.Remove(id);
            if (deleted)
            {
                logger.LogInformation("Deleted dynamic endpoint {Id}.", id);
            }

            return deleted;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DynamicEndpointDefinition> SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default)
    {
        var current = await WithStoreAsync(store => store.FindAsync(id, cancellationToken))
            ?? throw new DynamicEndpointNotFoundException(id);
        return current.Enabled == enabled
            ? current
            : await UpdateAsync(current with { Enabled = enabled }, cancellationToken);
    }

    public async Task<DynamicEndpointValidationResult> ValidateAsync(DynamicEndpointDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var errors = await CompileAsync(DefinitionNormalizer.Normalize(definition), cancellationToken);
        return new DynamicEndpointValidationResult(!errors.Errors.HasErrors, errors.Errors.ToDictionary());
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var definitions = await WithStoreAsync(store => store.GetAllAsync(cancellationToken));
            var entries = new List<RuntimeEntry>(definitions.Count);
            var active = new List<CompiledEndpoint>();

            // Oldest wins when the store contains clashing routes (e.g. edited by hand).
            foreach (var definition in definitions.OrderBy(d => d.CreatedAt).ThenBy(d => d.Id))
            {
                var existing = runtime.Find(definition.Id);
                CompiledEndpoint? compiled;
                List<string> errors;
                if (existing?.Compiled is not null &&
                    existing.Definition.Version == definition.Version &&
                    existing.Definition.UpdatedAt == definition.UpdatedAt)
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

                entries.Add(new RuntimeEntry(definition, compiled, errors));
            }

            runtime.Replace(entries);
            logger.LogDebug("Loaded {Count} dynamic endpoints ({Active} active).", entries.Count, active.Count);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<CompilationResult> CompileAsync(DynamicEndpointDefinition d, CancellationToken cancellationToken)
    {
        var result = await compiler.CompileAsync(d, cancellationToken);
        if (result.Endpoint is not null && d.Enabled)
        {
            foreach (var conflict in runtime.FindConflicts(result.Endpoint, runtime.ActiveEndpoints()))
            {
                result.Errors.Add("route", conflict);
            }
        }

        return result.Errors.HasErrors ? result with { Endpoint = null } : result;
    }

    private async Task<CompiledEndpoint> CompileOrThrowAsync(DynamicEndpointDefinition d, CancellationToken cancellationToken)
    {
        var result = await CompileAsync(d, cancellationToken);
        return result.Endpoint ?? throw new DynamicEndpointValidationException(result.Errors.ToDictionary());
    }

    private DynamicEndpointState ToState(DynamicEndpointDefinition definition)
    {
        var entry = runtime.Find(definition.Id);
        if (entry is null || entry.Definition.Version != definition.Version)
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
