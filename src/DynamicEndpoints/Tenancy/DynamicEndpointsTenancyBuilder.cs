using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicEndpoints;

/// <summary>Configures how the tenant of a request is found – see <c>UseMultiTenancy()</c>.</summary>
public sealed class DynamicEndpointsTenancyBuilder
{
    internal DynamicEndpointsTenancyBuilder(IServiceCollection services) => Services = services;

    public IServiceCollection Services { get; }

    /// <summary>Tenant from a request header.</summary>
    public DynamicEndpointsTenancyBuilder FromHeader(string headerName = "X-Tenant-Id") => From(new HeaderTenantResolver(headerName));

    /// <summary>Tenant from the host name (<c>acme.example.com</c> → <c>acme</c>), or whatever <paramref name="map"/> returns for the host.</summary>
    public DynamicEndpointsTenancyBuilder FromHost(Func<string, string?>? map = null) => From(new HostTenantResolver(map));

    /// <summary>Tenant from a claim of the (authenticated) user.</summary>
    public DynamicEndpointsTenancyBuilder FromClaim(string claimType = "tenant_id", string? authenticationScheme = null) =>
        From(new ClaimTenantResolver(claimType, authenticationScheme));

    /// <summary>
    /// Tenant from a route prefix: every dynamic endpoint is routed under <paramref name="prefix"/>
    /// (<c>/orders</c> → <c>/tenants/{tenant}/orders</c>) – see <see cref="DynamicEndpointsTenancyOptions.RoutePrefix"/>.
    /// </summary>
    public DynamicEndpointsTenancyBuilder FromRoutePrefix(string prefix = "/tenants/{tenant}")
    {
        var parameter = new DynamicEndpointsTenancyOptions { RoutePrefix = prefix }.RouteParameter!;
        Services.Configure<DynamicEndpointsOptions>(o => o.Tenancy.RoutePrefix = prefix);
        return From(new RouteValueTenantResolver(parameter));
    }

    /// <summary>Tenant from your own resolver (singleton; use <c>context.RequestServices</c> for scoped services).</summary>
    public DynamicEndpointsTenancyBuilder From<TResolver>()
        where TResolver : class, IDynamicEndpointTenantResolver
    {
        Services.AddSingleton<IDynamicEndpointTenantResolver, TResolver>();
        return this;
    }

    public DynamicEndpointsTenancyBuilder From(IDynamicEndpointTenantResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        Services.AddSingleton(resolver);
        return this;
    }

    public DynamicEndpointsTenancyBuilder From(Func<HttpContext, ValueTask<string?>> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        return From(new DelegateTenantResolver(resolve));
    }
}
