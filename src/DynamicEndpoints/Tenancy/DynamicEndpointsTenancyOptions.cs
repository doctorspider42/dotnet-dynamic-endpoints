using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing.Patterns;

namespace DynamicEndpoints;

/// <summary>Multi-tenancy settings – <see cref="DynamicEndpointsOptions.Tenancy"/>, set up by <c>UseMultiTenancy()</c>.</summary>
public sealed partial class DynamicEndpointsTenancyOptions
{
    private string? _routePrefix;

    /// <summary>
    /// Whether definitions may belong to a tenant (<see cref="DynamicEndpointDefinition.Tenant"/>). Switched on by
    /// <c>UseMultiTenancy()</c>; without it a definition with a tenant is rejected.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Route prefix with exactly one parameter that carries the tenant, e.g. <c>/tenants/{tenant}</c>. When set, every dynamic
    /// endpoint is routed under it (<c>/orders</c> → <c>/tenants/{tenant}/orders</c>): tenant endpoints for their own tenant only,
    /// shared endpoints for every tenant. Routes in definitions stay relative to the prefix.
    /// </summary>
    public string? RoutePrefix
    {
        get => _routePrefix;
        set
        {
            if (value is null)
            {
                _routePrefix = null;
                RouteParameter = null;
                return;
            }

            var prefix = "/" + value.Trim().Trim('/');
            var pattern = RoutePatternFactory.Parse(prefix);
            if (pattern.Parameters.Count != 1 || pattern.Parameters[0].IsCatchAll || pattern.Parameters[0].IsOptional)
            {
                throw new ArgumentException("The tenant route prefix needs exactly one required parameter, e.g. '/tenants/{tenant}'.", nameof(value));
            }

            _routePrefix = prefix;
            RouteParameter = pattern.Parameters[0].Name;
        }
    }

    /// <summary>Name of the route parameter in <see cref="RoutePrefix"/> (<c>tenant</c> for <c>/tenants/{tenant}</c>).</summary>
    public string? RouteParameter { get; private set; }

    /// <summary>Whether <paramref name="tenant"/> is a valid tenant id: letters, digits, <c>-</c>, <c>_</c> and <c>.</c>, at most 100 characters.</summary>
    public static bool IsValidTenant(string? tenant) => tenant is not null && TenantRegex().IsMatch(tenant);

    /// <summary>Tenant ids are compared case-insensitively.</summary>
    public static bool SameTenant(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,99}$")]
    private static partial Regex TenantRegex();
}
