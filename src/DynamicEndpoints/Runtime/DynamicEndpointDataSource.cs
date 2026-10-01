using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;

namespace DynamicEndpoints.Runtime;

/// <summary>
/// Endpoint source plugged into ASP.NET Core routing. Replacing the list and firing the change token makes
/// routing rebuild its matcher – endpoints appear and disappear without a restart.
/// </summary>
internal sealed class DynamicEndpointDataSource : EndpointDataSource
{
    private readonly Lock _lock = new();
    private IReadOnlyList<Endpoint> _endpoints = [];
    private CancellationTokenSource _changeTokenSource = new();

    public override IReadOnlyList<Endpoint> Endpoints => Volatile.Read(ref _endpoints);

    public override IChangeToken GetChangeToken()
    {
        lock (_lock)
        {
            return new CancellationChangeToken(_changeTokenSource.Token);
        }
    }

    public void Update(IReadOnlyList<Endpoint> endpoints)
    {
        CancellationTokenSource previous;
        lock (_lock)
        {
            Volatile.Write(ref _endpoints, endpoints);
            previous = _changeTokenSource;
            _changeTokenSource = new CancellationTokenSource();
        }

        previous.Cancel();
    }
}

/// <summary>Conventions applied to all dynamic endpoints – returned from <c>MapDynamicEndpoints()</c>.</summary>
internal sealed class DynamicEndpointConventions : IEndpointConventionBuilder
{
    private readonly List<Action<EndpointBuilder>> _conventions = [];
    private readonly List<Action<EndpointBuilder>> _finallyConventions = [];

    public void Add(Action<EndpointBuilder> convention)
    {
        lock (_conventions)
        {
            _conventions.Add(convention);
        }
    }

    public void Finally(Action<EndpointBuilder> finallyConvention)
    {
        lock (_conventions)
        {
            _finallyConventions.Add(finallyConvention);
        }
    }

    public void Apply(EndpointBuilder builder, Action<EndpointBuilder> beforeFinally)
    {
        Action<EndpointBuilder>[] conventions, finallyConventions;
        lock (_conventions)
        {
            conventions = [.. _conventions];
            finallyConventions = [.. _finallyConventions];
        }

        foreach (var convention in conventions)
        {
            convention(builder);
        }

        beforeFinally(builder);

        for (var i = finallyConventions.Length - 1; i >= 0; i--)
        {
            finallyConventions[i](builder);
        }
    }
}
