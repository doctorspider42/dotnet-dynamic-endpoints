using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicEndpoints;

/// <summary>
/// Finds the tenant of a request – from a header, the host, a claim, … Resolvers run in registration order, the first non-empty
/// result wins. Called during routing (before authentication middleware ran), at most once per request.
/// </summary>
public interface IDynamicEndpointTenantResolver
{
    /// <summary>The tenant id, or <c>null</c> when this resolver can't tell.</summary>
    ValueTask<string?> ResolveTenantAsync(HttpContext context);
}

/// <summary>Reads the tenant from a request header, e.g. <c>X-Tenant-Id</c>.</summary>
public sealed class HeaderTenantResolver(string headerName = "X-Tenant-Id") : IDynamicEndpointTenantResolver
{
    public string HeaderName { get; } = string.IsNullOrWhiteSpace(headerName)
        ? throw new ArgumentException("A header name is required.", nameof(headerName))
        : headerName;

    public ValueTask<string?> ResolveTenantAsync(HttpContext context) =>
        ValueTask.FromResult(context.Request.Headers[HeaderName].FirstOrDefault() is { Length: > 0 } value ? value.Trim() : null);
}

/// <summary>
/// Reads the tenant from the host name. By default it is the first label of hosts with at least three labels
/// (<c>acme.example.com</c> → <c>acme</c>) or of <c>*.localhost</c> (<c>acme.localhost</c>); pass <c>map</c> for anything else.
/// </summary>
public sealed class HostTenantResolver(Func<string, string?>? map = null) : IDynamicEndpointTenantResolver
{
    public ValueTask<string?> ResolveTenantAsync(HttpContext context)
    {
        var host = context.Request.Host.Host;
        if (string.IsNullOrEmpty(host))
        {
            return ValueTask.FromResult<string?>(null);
        }

        return ValueTask.FromResult(map is null ? FirstLabel(host) : map(host));
    }

    private static string? FirstLabel(string host)
    {
        var labels = host.Split('.');
        var isSubdomain = labels.Length >= 3 ||
            (labels.Length == 2 && string.Equals(labels[1], "localhost", StringComparison.OrdinalIgnoreCase));
        return isSubdomain && !System.Net.IPAddress.TryParse(host, out _) ? labels[0] : null;
    }
}

/// <summary>
/// Reads the tenant from a claim of the user. Routing runs before the authentication middleware, so an unauthenticated user is
/// authenticated here with <paramref name="authenticationScheme"/> (the default scheme when <c>null</c>).
/// </summary>
public sealed class ClaimTenantResolver(string claimType = "tenant_id", string? authenticationScheme = null) : IDynamicEndpointTenantResolver
{
    public string ClaimType { get; } = string.IsNullOrWhiteSpace(claimType)
        ? throw new ArgumentException("A claim type is required.", nameof(claimType))
        : claimType;

    public async ValueTask<string?> ResolveTenantAsync(HttpContext context)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated != true && context.RequestServices.GetService<IAuthenticationService>() is not null)
        {
            var result = await context.AuthenticateAsync(authenticationScheme);
            user = result.Succeeded ? result.Principal : user;
        }

        return Find(user);
    }

    private string? Find(ClaimsPrincipal? user) =>
        user?.FindFirst(ClaimType)?.Value is { Length: > 0 } value ? value : null;
}

/// <summary>Reads the tenant from the route value of <see cref="DynamicEndpointsTenancyOptions.RoutePrefix"/> (or any other route parameter).</summary>
public sealed class RouteValueTenantResolver(string parameterName = "tenant") : IDynamicEndpointTenantResolver
{
    public string ParameterName { get; } = parameterName;

    public ValueTask<string?> ResolveTenantAsync(HttpContext context) =>
        ValueTask.FromResult(context.Request.RouteValues.TryGetValue(ParameterName, out var value) && value?.ToString() is { Length: > 0 } tenant
            ? tenant
            : null);
}

internal sealed class DelegateTenantResolver(Func<HttpContext, ValueTask<string?>> resolve) : IDynamicEndpointTenantResolver
{
    public ValueTask<string?> ResolveTenantAsync(HttpContext context) => resolve(context);
}
