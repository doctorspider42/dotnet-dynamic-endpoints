namespace DynamicEndpoints;

/// <summary>Non-persistent store with history and drafts – the default. Useful for tests and prototyping.</summary>
public sealed class InMemoryDynamicEndpointStore : IDynamicEndpointStore, IDynamicEndpointRevisionStore
{
    private readonly Dictionary<Guid, DynamicEndpointDefinition> _definitions = [];
    private readonly Dictionary<Guid, SortedList<int, DynamicEndpointRevision>> _revisions = [];
    private readonly Dictionary<Guid, DynamicEndpointDraft> _drafts = [];

    // Revisions wait here until the write of their definition succeeded – a failed write leaves no trace in the history.
    private readonly Dictionary<(Guid, int), DynamicEndpointRevision> _pending = [];
    private readonly Lock _lock = new();

    public Task<IReadOnlyList<DynamicEndpointDefinition>> GetAllAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<DynamicEndpointDefinition>>(
                _definitions.Values.Select(DynamicEndpointsJson.DeepClone).ToList());
        }
    }

    public Task<DynamicEndpointDefinition?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_definitions.TryGetValue(id, out var d) ? DynamicEndpointsJson.DeepClone(d) : null);
        }
    }

    public Task AddAsync(DynamicEndpointDefinition definition, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_definitions.TryAdd(definition.Id, DynamicEndpointsJson.DeepClone(definition)))
            {
                throw new InvalidOperationException($"Dynamic endpoint '{definition.Id}' already exists.");
            }

            CommitRevision(definition);
        }

        return Task.CompletedTask;
    }

    public Task UpdateAsync(DynamicEndpointDefinition definition, int expectedRevision, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_definitions.TryGetValue(definition.Id, out var current))
            {
                throw new DynamicEndpointNotFoundException(definition.Id);
            }

            if (current.Revision != expectedRevision)
            {
                throw new DynamicEndpointConcurrencyException(definition.Id, expectedRevision, current.Revision);
            }

            _definitions[definition.Id] = DynamicEndpointsJson.DeepClone(definition);
            CommitRevision(definition);
        }

        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_definitions.Remove(id))
            {
                return Task.FromResult(false);
            }

            _revisions.Remove(id);
            _drafts.Remove(id);
            return Task.FromResult(true);
        }
    }

    public Task AddRevisionAsync(DynamicEndpointRevision revision, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _pending[(revision.EndpointId, revision.Revision)] = DynamicEndpointsJson.DeepClone(revision);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DynamicEndpointRevision>> GetRevisionsAsync(Guid endpointId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<DynamicEndpointRevision>>(_revisions.TryGetValue(endpointId, out var revisions)
                ? revisions.Values.Reverse().Select(DynamicEndpointsJson.DeepClone).ToList()
                : []);
        }
    }

    public Task<DynamicEndpointRevision?> FindRevisionAsync(Guid endpointId, int revision, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_revisions.TryGetValue(endpointId, out var revisions) && revisions.TryGetValue(revision, out var r)
                ? DynamicEndpointsJson.DeepClone(r)
                : null);
        }
    }

    public Task<IReadOnlyList<DynamicEndpointDraft>> GetDraftsAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<DynamicEndpointDraft>>(_drafts.Values.Select(DynamicEndpointsJson.DeepClone).ToList());
        }
    }

    public Task<DynamicEndpointDraft?> FindDraftAsync(Guid endpointId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_drafts.TryGetValue(endpointId, out var draft) ? DynamicEndpointsJson.DeepClone(draft) : null);
        }
    }

    public Task SaveDraftAsync(DynamicEndpointDraft draft, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _drafts[draft.EndpointId] = DynamicEndpointsJson.DeepClone(draft);
        }

        return Task.CompletedTask;
    }

    public Task<bool> DeleteDraftAsync(Guid endpointId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_drafts.Remove(endpointId));
        }
    }

    // Called under _lock after a definition was written: its revision and any recorded before it (e.g. the previous one, when
    // the history started after it was saved) become part of the history.
    private void CommitRevision(DynamicEndpointDefinition definition)
    {
        foreach (var key in _pending.Keys.Where(k => k.Item1 == definition.Id && k.Item2 <= definition.Revision).ToList())
        {
            _pending.Remove(key, out var revision);
            if (!_revisions.TryGetValue(definition.Id, out var revisions))
            {
                _revisions[definition.Id] = revisions = [];
            }

            revisions[revision!.Revision] = revision;
            if (revision.Kind == DynamicEndpointRevisionKind.Published)
            {
                _drafts.Remove(definition.Id);
            }
        }
    }
}
