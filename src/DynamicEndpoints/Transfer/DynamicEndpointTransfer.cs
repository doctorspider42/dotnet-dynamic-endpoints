using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicEndpoints.Runtime;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicEndpoints;

/// <summary>Exports definitions and imports them back – create, upsert or sync, with a dry run that reports the diff.</summary>
public interface IDynamicEndpointTransfer
{
    /// <summary>All definitions, or those with the given ids, in the stable export format.</summary>
    Task<DynamicEndpointExport> ExportAsync(IReadOnlyCollection<Guid>? ids = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports definitions. Endpoints are matched by <see cref="DynamicEndpointDefinition.Id"/>, or by method and route when the
    /// import has no id. The whole import is validated first – including route conflicts among the imported endpoints – and nothing
    /// is written when any endpoint is invalid. Writes go through the registered store one by one (not in one transaction); the
    /// routing table is updated once at the end.
    /// </summary>
    Task<DynamicEndpointImportResult> ImportAsync(DynamicEndpointExport import, DynamicEndpointImportOptions? options = null, CancellationToken cancellationToken = default);
}

internal sealed class DynamicEndpointTransfer(
    IDynamicEndpointManager manager,
    IServiceScopeFactory scopeFactory,
    ILogger<DynamicEndpointTransfer> logger) : IDynamicEndpointTransfer
{
    private static readonly string[] StoreProperties = ["id", "revision", "createdAt", "updatedAt"];

    /// <summary>The same transfer on the tenant's view of the manager: it exports, matches and syncs only that tenant's endpoints.</summary>
    internal DynamicEndpointTransfer ForTenant(string? tenant) => new(manager.ForTenant(tenant), scopeFactory, logger);

    public async Task<DynamicEndpointExport> ExportAsync(IReadOnlyCollection<Guid>? ids = null, CancellationToken cancellationToken = default)
    {
        var definitions = await PublishedAsync(cancellationToken);
        if (ids is { Count: > 0 })
        {
            var selected = ids.ToHashSet();
            definitions = definitions.Where(d => selected.Contains(d.Id));
        }

        return new DynamicEndpointExport { Endpoints = DynamicEndpointExport.Sort(definitions).ToList() };
    }

    public async Task<DynamicEndpointImportResult> ImportAsync(
        DynamicEndpointExport import,
        DynamicEndpointImportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(import);
        options ??= new DynamicEndpointImportOptions();
        var current = (await PublishedAsync(cancellationToken)).ToList();
        var plan = Plan(import, options.Mode, current);

        // Rehearsal: the real change set logic against a store that only remembers the writes.
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var rehearsal = new OverlayDynamicEndpointStore(scope.ServiceProvider.GetRequiredService<IDynamicEndpointStore>());
            await ExecuteAsync(manager.BeginChanges(rehearsal), plan, cancellationToken);
        }

        var succeeded = plan.All(p => p.Item.Action != DynamicEndpointImportAction.Invalid);
        if (options.DryRun || !succeeded || !plan.Any(p => p.Writes))
        {
            return Result(plan, options, succeeded);
        }

        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var changes = manager.BeginChanges(scope.ServiceProvider.GetRequiredService<IDynamicEndpointStore>());
            try
            {
                await ExecuteAsync(changes, plan, cancellationToken);
            }
            finally
            {
                // Whatever reached the store must reach the routing table too, even when a later write failed.
                await changes.ApplyAsync(CancellationToken.None);
            }
        }

        succeeded = plan.All(p => p.Item.Action != DynamicEndpointImportAction.Invalid);
        logger.LogInformation("Imported dynamic endpoints ({Mode}): {Created} created, {Updated} updated, {Deleted} deleted.",
            options.Mode, plan.Count(p => p.Item.Action == DynamicEndpointImportAction.Create),
            plan.Count(p => p.Item.Action == DynamicEndpointImportAction.Update),
            plan.Count(p => p.Item.Action == DynamicEndpointImportAction.Delete));
        return Result(plan, options, succeeded);
    }

    // Drafts that were never published aren't stored endpoints: not exported, not matched, not deleted by a sync.
    private async Task<IEnumerable<DynamicEndpointDefinition>> PublishedAsync(CancellationToken cancellationToken) =>
        (await manager.ListAsync(cancellationToken)).Where(s => s.Status != DynamicEndpointStatus.Draft).Select(s => s.Definition);

    private static DynamicEndpointImportResult Result(List<PlannedChange> plan, DynamicEndpointImportOptions options, bool succeeded) => new()
    {
        Succeeded = succeeded,
        DryRun = options.DryRun,
        Mode = options.Mode,
        Items = plan.Select(p => p.Item).ToList(),
    };

    private static List<PlannedChange> Plan(DynamicEndpointExport import, DynamicEndpointImportMode mode, List<DynamicEndpointDefinition> current)
    {
        var byId = current.ToDictionary(d => d.Id);
        var matched = new HashSet<Guid>();
        var seen = new HashSet<Guid>();
        var plan = new List<PlannedChange>();

        foreach (var incoming in import.Endpoints)
        {
            var d = DefinitionNormalizer.Normalize(incoming) with { Revision = 0, CreatedAt = default, UpdatedAt = default };
            DynamicEndpointDefinition? existing = null;
            if (d.Id != Guid.Empty)
            {
                existing = byId.GetValueOrDefault(d.Id);
            }
            else if (RouteKey(d) is { } key)
            {
                var candidates = current.Where(c => !matched.Contains(c.Id) && c.Method == d.Method && RouteKey(c) == key).ToList();
                existing = candidates.Count == 1 ? candidates[0] : null;
            }

            var id = existing?.Id ?? (d.Id == Guid.Empty ? Guid.CreateVersion7() : d.Id);
            d = d with { Id = id };
            var item = new DynamicEndpointImportItem { Id = id, Method = d.Method, Route = d.Route, Name = d.Name };

            if (!seen.Add(id))
            {
                plan.Add(new PlannedChange(item with
                {
                    Action = DynamicEndpointImportAction.Invalid,
                    Errors = new Dictionary<string, string[]> { ["id"] = [$"Endpoint '{id}' is imported more than once."] },
                }, d));
                continue;
            }

            if (existing is not null)
            {
                matched.Add(existing.Id);
                var changes = Diff(DefinitionNormalizer.Normalize(existing), d);
                var action = mode == DynamicEndpointImportMode.Create ? DynamicEndpointImportAction.Skip
                    : changes.Count == 0 ? DynamicEndpointImportAction.Unchanged
                    : DynamicEndpointImportAction.Update;
                plan.Add(new PlannedChange(item with { Action = action, Changes = action == DynamicEndpointImportAction.Update ? changes : [] }, d));
            }
            else
            {
                plan.Add(new PlannedChange(item with { Action = DynamicEndpointImportAction.Create }, d));
            }
        }

        if (mode == DynamicEndpointImportMode.Sync)
        {
            foreach (var orphan in DynamicEndpointExport.Sort(current.Where(c => !matched.Contains(c.Id))))
            {
                plan.Add(new PlannedChange(new DynamicEndpointImportItem
                {
                    Action = DynamicEndpointImportAction.Delete,
                    Id = orphan.Id,
                    Method = orphan.Method,
                    Route = orphan.Route,
                    Name = orphan.Name,
                }, orphan));
            }
        }

        return plan;
    }

    // Deletes first (they free routes), then updates, then creates.
    private static async Task ExecuteAsync(DynamicEndpointChangeSet changes, List<PlannedChange> plan, CancellationToken cancellationToken)
    {
        foreach (var action in (DynamicEndpointImportAction[])[DynamicEndpointImportAction.Delete, DynamicEndpointImportAction.Update, DynamicEndpointImportAction.Create])
        {
            foreach (var change in plan.Where(p => p.Item.Action == action))
            {
                try
                {
                    switch (action)
                    {
                        case DynamicEndpointImportAction.Delete:
                            await changes.DeleteAsync(change.Definition.Id, cancellationToken);
                            break;
                        case DynamicEndpointImportAction.Update:
                            await changes.UpsertAsync(change.Definition, cancellationToken);
                            break;
                        default:
                            await changes.CreateAsync(change.Definition, cancellationToken);
                            break;
                    }
                }
                catch (DynamicEndpointValidationException ex)
                {
                    change.Item = change.Item with { Action = DynamicEndpointImportAction.Invalid, Errors = ex.Errors };
                }
                catch (DynamicEndpointException ex)
                {
                    change.Item = change.Item with
                    {
                        Action = DynamicEndpointImportAction.Invalid,
                        Errors = new Dictionary<string, string[]> { ["id"] = [ex.Message] },
                    };
                }
            }
        }
    }

    /// <summary>Top-level properties whose content differs.</summary>
    internal static List<string> Diff(DynamicEndpointDefinition current, DynamicEndpointDefinition incoming)
    {
        var a = Content(current);
        var b = Content(incoming);
        return a.Select(p => p.Key).Union(b.Select(p => p.Key))
            .Where(key => !JsonNode.DeepEquals(a[key], b[key]))
            .ToList();
    }

    private static JsonObject Content(DynamicEndpointDefinition d)
    {
        var node = (JsonObject)JsonSerializer.SerializeToNode(d, DynamicEndpointsJson.SerializerOptions)!;
        foreach (var property in StoreProperties)
        {
            node.Remove(property);
        }

        return node;
    }

    private static string? RouteKey(DynamicEndpointDefinition d)
    {
        try
        {
            return RouteKeys.Normalize(RoutePatternFactory.Parse(d.Route));
        }
        catch (RoutePatternException)
        {
            return null;
        }
    }

    private sealed class PlannedChange(DynamicEndpointImportItem item, DynamicEndpointDefinition definition)
    {
        public DynamicEndpointImportItem Item { get; set; } = item;

        public DynamicEndpointDefinition Definition { get; } = definition;

        public bool Writes => Item.Action is DynamicEndpointImportAction.Create or DynamicEndpointImportAction.Update or DynamicEndpointImportAction.Delete;
    }
}

/// <summary>A store that reads through to another one and keeps its own writes in memory – for dry runs.</summary>
internal sealed class OverlayDynamicEndpointStore(IDynamicEndpointStore inner) : IDynamicEndpointStore
{
    private readonly Dictionary<Guid, DynamicEndpointDefinition?> _writes = [];

    public async Task<IReadOnlyList<DynamicEndpointDefinition>> GetAllAsync(CancellationToken cancellationToken)
    {
        var all = (await inner.GetAllAsync(cancellationToken)).ToDictionary(d => d.Id);
        foreach (var (id, definition) in _writes)
        {
            if (definition is null)
            {
                all.Remove(id);
            }
            else
            {
                all[id] = definition;
            }
        }

        return all.Values.ToList();
    }

    public async Task<DynamicEndpointDefinition?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        _writes.TryGetValue(id, out var written) ? written : await inner.FindAsync(id, cancellationToken);

    public Task AddAsync(DynamicEndpointDefinition definition, CancellationToken cancellationToken)
    {
        _writes[definition.Id] = definition;
        return Task.CompletedTask;
    }

    public async Task UpdateAsync(DynamicEndpointDefinition definition, int expectedRevision, CancellationToken cancellationToken)
    {
        var current = await FindAsync(definition.Id, cancellationToken) ?? throw new DynamicEndpointNotFoundException(definition.Id);
        if (current.Revision != expectedRevision)
        {
            throw new DynamicEndpointConcurrencyException(definition.Id, expectedRevision, current.Revision);
        }

        _writes[definition.Id] = definition;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await FindAsync(id, cancellationToken) is null)
        {
            return false;
        }

        _writes[id] = null;
        return true;
    }
}
