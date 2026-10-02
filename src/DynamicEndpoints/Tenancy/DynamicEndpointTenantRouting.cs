using DynamicEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DynamicEndpoints.Tenancy;

/// <summary>Runs the tenant resolvers once per request and remembers the result.</summary>
internal sealed class DynamicEndpointTenantResolution(IEnumerable<IDynamicEndpointTenantResolver> resolvers)
{
    private readonly IDynamicEndpointTenantResolver[] _resolvers = resolvers.ToArray();

    public async ValueTask<string?> ResolveAsync(HttpContext context)
    {
        if (context.Features.Get<TenantFeature>() is { } feature)
        {
            return feature.Tenant;
        }

        string? tenant = null;
        foreach (var resolver in _resolvers)
        {
            if ((await resolver.ResolveTenantAsync(context))?.Trim() is { Length: > 0 } value)
            {
                tenant = value;
                break;
            }
        }

        return Set(context, tenant);
    }

    /// <summary>Remembers the tenant of the request; ids that are not valid tenant ids count as no tenant.</summary>
    public static string? Set(HttpContext context, string? tenant)
    {
        tenant = DynamicEndpointsTenancyOptions.IsValidTenant(tenant) ? tenant : null;
        context.Features.Set(new TenantFeature(tenant));
        return tenant;
    }

    private sealed record TenantFeature(string? Tenant);
}

/// <summary>
/// Makes endpoints of a tenant routable only for requests of that tenant – several tenants can use the same route, and routing
/// picks the right one. Shared endpoints (no tenant) are left alone.
/// </summary>
internal sealed class DynamicEndpointTenantMatcherPolicy(
    DynamicEndpointTenantResolution resolution,
    IOptions<DynamicEndpointsOptions> options) : MatcherPolicy, IEndpointSelectorPolicy
{
    // After the built-in policies (HTTP method, host, content type), so the resolvers only run for routes that match otherwise.
    public override int Order => 10_000;

    public bool AppliesToEndpoints(IReadOnlyList<Endpoint> endpoints)
    {
        foreach (var endpoint in endpoints)
        {
            if (endpoint.Metadata.GetMetadata<DynamicEndpointMetadata>()?.Definition.Tenant is not null)
            {
                return true;
            }
        }

        return false;
    }

    public async Task ApplyAsync(HttpContext httpContext, CandidateSet candidates)
    {
        var routeParameter = options.Value.Tenancy.RouteParameter;
        string? tenant = null;
        var resolved = false;
        for (var i = 0; i < candidates.Count; i++)
        {
            if (!candidates.IsValidCandidate(i) ||
                candidates[i].Endpoint.Metadata.GetMetadata<DynamicEndpointMetadata>()?.Definition.Tenant is not { } owner)
            {
                continue;
            }

            if (!resolved)
            {
                // With a route prefix the tenant is part of the path; otherwise ask the resolvers.
                tenant = routeParameter is not null
                    ? DynamicEndpointTenantResolution.Set(httpContext, candidates[i].Values?[routeParameter]?.ToString())
                    : await resolution.ResolveAsync(httpContext);
                resolved = true;
            }

            if (!DynamicEndpointsTenancyOptions.SameTenant(owner, tenant))
            {
                candidates.SetValidity(i, false);
            }
        }
    }
}
