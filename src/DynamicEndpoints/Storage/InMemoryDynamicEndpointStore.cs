namespace DynamicEndpoints;

/// <summary>Non-persistent store – the default. Useful for tests and prototyping.</summary>
public sealed class InMemoryDynamicEndpointStore : IDynamicEndpointStore
{
    private readonly Dictionary<Guid, DynamicEndpointDefinition> _definitions = [];
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
        }

        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_definitions.Remove(id));
        }
    }
}
