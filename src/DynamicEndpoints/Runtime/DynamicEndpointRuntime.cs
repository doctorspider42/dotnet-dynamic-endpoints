using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace DynamicEndpoints.Runtime;

internal sealed record RuntimeEntry(DynamicEndpointDefinition Definition, CompiledEndpoint? Compiled, IReadOnlyList<string> Errors)
{
    public bool IsActive => Compiled is not null && Definition.Enabled && Errors.Count == 0;
}

/// <summary>In-memory state of this instance: which definitions are loaded and which are routable.</summary>
internal sealed class DynamicEndpointRuntime(
    DynamicEndpointDataSource dataSource,
    DynamicEndpointConventions conventions,
    DynamicRequestHandler handler,
    RouteInspector routes,
    IOptions<DynamicEndpointsOptions> options)
{
    private readonly Lock _lock = new();
    private Dictionary<Guid, RuntimeEntry> _entries = [];

    public IReadOnlyCollection<RuntimeEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return _entries.Values.ToList();
            }
        }
    }

    public RuntimeEntry? Find(Guid id)
    {
        lock (_lock)
        {
            return _entries.GetValueOrDefault(id);
        }
    }

    public void Replace(IEnumerable<RuntimeEntry> entries)
    {
        lock (_lock)
        {
            _entries = entries.ToDictionary(e => e.Definition.Id);
            Publish();
        }
    }

    public void Upsert(RuntimeEntry entry)
    {
        lock (_lock)
        {
            _entries = new Dictionary<Guid, RuntimeEntry>(_entries) { [entry.Definition.Id] = entry };
            Publish();
        }
    }

    /// <summary>Applies several changes and publishes the routing table once.</summary>
    public void Apply(Action<Dictionary<Guid, RuntimeEntry>> change)
    {
        lock (_lock)
        {
            var entries = new Dictionary<Guid, RuntimeEntry>(_entries);
            change(entries);
            _entries = entries;
            Publish();
        }
    }

    public void Remove(Guid id)
    {
        lock (_lock)
        {
            var entries = new Dictionary<Guid, RuntimeEntry>(_entries);
            if (entries.Remove(id))
            {
                _entries = entries;
                Publish();
            }
        }
    }

    /// <summary>Route clashes of <paramref name="candidate"/> with other dynamic endpoints and with the application's own endpoints.</summary>
    public IEnumerable<string> FindConflicts(CompiledEndpoint candidate, IEnumerable<CompiledEndpoint> activeDynamicEndpoints)
    {
        foreach (var other in activeDynamicEndpoints)
        {
            if (other.Definition.Id != candidate.Definition.Id && other.RouteKey == candidate.RouteKey)
            {
                yield return $"Route conflicts with dynamic endpoint '{other.Definition.Name ?? other.Definition.Route}' ({other.Definition.Id}).";
            }
        }

        var method = candidate.Definition.Method;
        var key = RouteKeys.Normalize(candidate.RoutePattern);
        foreach (var (staticMethod, staticKey, display) in routes.GetStaticRoutes())
        {
            if (staticKey == key && (staticMethod == "*" || staticMethod == method))
            {
                yield return $"Route conflicts with application endpoint '{display}'.";
            }
        }
    }

    public IEnumerable<CompiledEndpoint> ActiveEndpoints() =>
        Entries.Where(e => e.IsActive).Select(e => e.Compiled!);

    // Called under _lock. Builds the complete list and swaps it in one assignment – never a half-applied state.
    private void Publish()
    {
        var endpoints = _entries.Values
            .Where(e => e.IsActive)
            .Select(e => Build(e.Compiled!))
            .ToList();
        dataSource.Update(endpoints);
    }

    private Endpoint Build(CompiledEndpoint compiled)
    {
        var d = compiled.Definition;
        var builder = new RouteEndpointBuilder(
            context => handler.HandleAsync(context, compiled),
            compiled.RoutePattern,
            order: 0)
        {
            DisplayName = $"Dynamic {d.Method} {d.Route}" + (d.Name is null ? string.Empty : $" ({d.Name})"),
        };

        builder.Metadata.Add(new HttpMethodMetadata([d.Method]));
        builder.Metadata.Add(compiled.Metadata);

        if (d.AllowAnonymous)
        {
            builder.Metadata.Add(new AllowAnonymousAttribute());
        }
        else if (d.AuthorizationPolicy is not null || d.RequireAuthorization)
        {
            builder.Metadata.Add(new AuthorizeAttribute { Policy = d.AuthorizationPolicy });
        }

        if (d.RateLimitingPolicy is not null)
        {
            builder.Metadata.Add(new EnableRateLimitingAttribute(d.RateLimitingPolicy));
        }

        conventions.Apply(builder, b => options.Value.ConfigureEndpoint?.Invoke(b, d));
        return builder.Build();
    }
}
