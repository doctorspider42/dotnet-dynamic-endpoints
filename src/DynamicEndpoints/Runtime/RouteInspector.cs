using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace DynamicEndpoints.Runtime;

/// <summary>Knows the routes owned by the host application: reserved prefixes and statically mapped endpoints.</summary>
internal sealed class RouteInspector(IOptions<DynamicEndpointsOptions> options)
{
    private readonly List<string> _reserved = [];
    private readonly ConcurrentDictionary<string, Regex> _prefixPatterns = new(StringComparer.Ordinal);
    private ICollection<EndpointDataSource>? _dataSources;
    private EndpointDataSource? _own;

    public bool IsAttached => _dataSources is not null;

    public void Attach(ICollection<EndpointDataSource> dataSources, EndpointDataSource own)
    {
        _dataSources = dataSources;
        _own = own;
    }

    public void Reserve(string prefix)
    {
        lock (_reserved)
        {
            _reserved.Add(prefix);
        }
    }

    /// <summary>Whether <paramref name="route"/> starts with one of <see cref="DynamicEndpointsOptions.RequiredRoutePrefixes"/> (or none are configured).</summary>
    public bool HasRequiredPrefix(string route)
    {
        var prefixes = options.Value.RequiredRoutePrefixes;
        return prefixes.Count == 0 || prefixes.Any(prefix => _prefixPatterns.GetOrAdd(prefix, ToPrefixRegex).IsMatch(route));
    }

    // "/api/v{version:int}" -> ^/api/v[0-9]+(?:/|$). Parameters match a literal part of a single segment, never route parameters.
    private static Regex ToPrefixRegex(string prefix)
    {
        var normalized = "/" + prefix.Trim().Trim('/');
        var builder = new StringBuilder("^");
        var i = 0;
        while (i < normalized.Length)
        {
            if (normalized[i] == '{' && normalized.IndexOf('}', i) is var end and > 0)
            {
                var constraint = normalized[(i + 1)..end].Split(':', 2) is [_, var c] ? c : null;
                builder.Append(constraint is "int" or "long" ? "[0-9]+" : constraint is "alpha" ? "[A-Za-z]+" : "[^/{}]+");
                i = end + 1;
            }
            else
            {
                builder.Append(Regex.Escape(normalized[i].ToString()));
                i++;
            }
        }

        builder.Append(normalized == "/" ? string.Empty : "(?:/|$)");
        return new Regex(builder.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }

    public string? FindReservedPrefix(string route)
    {
        string[] reserved;
        lock (_reserved)
        {
            reserved = [.. options.Value.ReservedPrefixes, .. _reserved];
        }

        foreach (var raw in reserved)
        {
            var prefix = "/" + raw.Trim().Trim('/');
            if (prefix == "/")
            {
                continue;
            }

            if (route.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                (route.Length == prefix.Length || route[prefix.Length] == '/'))
            {
                return prefix;
            }
        }

        return null;
    }

    /// <summary>Endpoints mapped by the application itself (minimal APIs, controllers, health checks, …).</summary>
    public IEnumerable<(string Method, string RouteKey, string DisplayName)> GetStaticRoutes()
    {
        if (_dataSources is null)
        {
            yield break;
        }

        foreach (var dataSource in _dataSources.ToArray())
        {
            if (ReferenceEquals(dataSource, _own))
            {
                continue;
            }

            IReadOnlyList<Endpoint> endpoints;
            try
            {
                endpoints = dataSource.Endpoints;
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            foreach (var endpoint in endpoints.OfType<RouteEndpoint>())
            {
                if (endpoint.Metadata.GetMetadata<DynamicEndpointMetadata>() is not null)
                {
                    continue;
                }

                var key = RouteKeys.Normalize(endpoint.RoutePattern);
                var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
                var display = endpoint.DisplayName ?? endpoint.RoutePattern.RawText ?? key;
                if (methods is null || methods.Count == 0)
                {
                    yield return ("*", key, display);
                    continue;
                }

                foreach (var method in methods)
                {
                    yield return (method.ToUpperInvariant(), key, display);
                }
            }
        }
    }
}
