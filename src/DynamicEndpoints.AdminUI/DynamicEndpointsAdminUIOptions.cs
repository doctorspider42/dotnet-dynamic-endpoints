namespace DynamicEndpoints;

/// <summary>Options of <c>MapDynamicEndpointsAdminUI</c>.</summary>
/// <remarks>
/// Links may contain route parameters of the panel's own pattern – <c>{tenant}</c> in <c>/tenants/{tenant}/admin</c> – which are
/// filled in from the request, like the admin API path.
/// </remarks>
public sealed class DynamicEndpointsAdminUIOptions
{
    /// <summary>Shown in the header and the browser tab.</summary>
    public string Title { get; set; } = "Dynamic Endpoints";

    /// <summary>Link to Swagger UI (or Scalar, …) in the header and the "Try" console; <c>null</c> hides it. Relative to the path base.</summary>
    public string? SwaggerUrl { get; set; }

    /// <summary>Link to the OpenAPI document (<c>MapDynamicEndpointsOpenApi</c>); <c>null</c> hides it. Relative to the path base.</summary>
    public string? OpenApiUrl { get; set; }

    /// <summary>
    /// With multi-tenancy: the OpenAPI document of one tenant, with a <c>{tenant}</c> placeholder – e.g. <c>/openapi/{tenant}/dynamic.json</c>
    /// for <c>MapDynamicEndpointsOpenApi("/openapi/{tenant}/dynamic.json")</c>. The link follows the tenant picked in the panel;
    /// <see cref="OpenApiUrl"/> stays the link for all tenants / the shared endpoints.
    /// </summary>
    public string? TenantOpenApiUrl { get; set; }

    /// <summary>With multi-tenancy: Swagger UI (or Scalar, …) of one tenant, with a <c>{tenant}</c> placeholder. See <see cref="TenantOpenApiUrl"/>.</summary>
    public string? TenantSwaggerUrl { get; set; }
}
