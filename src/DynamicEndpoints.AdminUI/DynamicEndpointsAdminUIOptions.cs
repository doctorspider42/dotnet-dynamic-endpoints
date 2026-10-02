namespace DynamicEndpoints;

/// <summary>Options of <c>MapDynamicEndpointsAdminUI</c>.</summary>
public sealed class DynamicEndpointsAdminUIOptions
{
    /// <summary>Shown in the header and the browser tab.</summary>
    public string Title { get; set; } = "Dynamic Endpoints";

    /// <summary>Link to Swagger UI (or Scalar, …) in the header and the "Try" console; <c>null</c> hides it. Relative to the path base.</summary>
    public string? SwaggerUrl { get; set; }

    /// <summary>Link to the OpenAPI document (<c>MapDynamicEndpointsOpenApi</c>); <c>null</c> hides it. Relative to the path base.</summary>
    public string? OpenApiUrl { get; set; }
}
