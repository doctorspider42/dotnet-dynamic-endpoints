namespace DynamicEndpoints;

/// <summary>
/// What the admin API of this server supports – <c>GET /info</c>. Clients such as the admin panel read it to show only the
/// features that work: tenancy, the audit log, drafts and history, export formats.
/// </summary>
public sealed record DynamicEndpointsAdminInfo
{
    /// <summary>Multi-tenancy settings, or <c>null</c> without <c>UseMultiTenancy()</c>.</summary>
    public DynamicEndpointsAdminTenancyInfo? Tenancy { get; init; }

    /// <summary>The tenant this admin API is scoped to (<c>MapDynamicEndpointsTenantAdmin</c>), or <c>null</c> for the full admin API.</summary>
    public string? Tenant { get; init; }

    /// <summary>The store keeps drafts and history (<see cref="IDynamicEndpointRevisionStore"/>).</summary>
    public bool Revisions { get; init; }

    /// <summary>A queryable audit log is configured – <c>GET /audit</c> and <c>GET /{id}/audit</c> answer.</summary>
    public bool AuditLog { get; init; }

    /// <summary>Text formats of <c>/export</c> (<c>?format=</c>), <c>/import</c> and <c>/import/openapi</c>, e.g. <c>json</c>, <c>yaml</c>.</summary>
    public IReadOnlyList<string> Formats { get; init; } = [];
}

/// <summary>How requests are assigned to tenants – part of <see cref="DynamicEndpointsAdminInfo"/>.</summary>
/// <param name="RoutePrefix">The tenant route prefix (<c>/tenants/{tenant}</c>) every dynamic endpoint is routed under, if any.</param>
/// <param name="RouteParameter">Name of the tenant parameter in <paramref name="RoutePrefix"/>.</param>
/// <param name="Header">Request header of the first <see cref="HeaderTenantResolver"/>, if any.</param>
public sealed record DynamicEndpointsAdminTenancyInfo(string? RoutePrefix, string? RouteParameter, string? Header);
