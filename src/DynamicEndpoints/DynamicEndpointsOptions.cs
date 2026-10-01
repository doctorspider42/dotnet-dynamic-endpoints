using Microsoft.AspNetCore.Builder;

namespace DynamicEndpoints;

public sealed class DynamicEndpointsOptions
{
    /// <summary>
    /// Route prefixes dynamic endpoints may never use (e.g. <c>/admin</c>, <c>/swagger</c>).
    /// Prefixes passed to <c>MapDynamicEndpointsAdmin</c> are added automatically.
    /// Exact clashes with any other endpoint of the application are detected regardless of this list.
    /// </summary>
    public IList<string> ReservedPrefixes { get; } = new List<string>();

    /// <summary>HTTP methods admins can use.</summary>
    public ISet<string> AllowedMethods { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "POST", "PUT", "PATCH", "DELETE",
    };

    /// <summary>Processor used by definitions that do not name one – handy for the "single entry point" setup.</summary>
    public string? DefaultProcessor { get; set; }

    /// <summary>Maximum accepted JSON body size in bytes. Default 1 MB.</summary>
    public long MaxRequestBodySize { get; set; } = 1024 * 1024;

    /// <summary>Maximum nesting depth of JSON bodies. Default 32.</summary>
    public int MaxJsonDepth { get; set; } = 32;

    /// <summary>
    /// When set, every instance re-reads definitions from the store at this interval –
    /// the simplest way to propagate changes across a multi-instance deployment.
    /// Call <see cref="IDynamicEndpointManager.ReloadAsync"/> yourself for push based propagation (e.g. Redis pub/sub).
    /// </summary>
    public TimeSpan? RefreshInterval { get; set; }

    /// <summary>Fail application start when definitions cannot be loaded from the store. Default <c>true</c>.</summary>
    public bool ThrowOnStartupLoadFailure { get; set; } = true;

    /// <summary>Hook to add custom metadata (CORS, output caching, …) to every built endpoint.</summary>
    public Action<EndpointBuilder, DynamicEndpointDefinition>? ConfigureEndpoint { get; set; }

    public DynamicEndpointsOpenApiOptions OpenApi { get; } = new();
}

public sealed class DynamicEndpointsOpenApiOptions
{
    public string Title { get; set; } = "Dynamic endpoints";

    public string Version { get; set; } = "v1";

    public string? Description { get; set; }

    /// <summary>Documentation section of endpoints without a <see cref="DynamicEndpointDefinition.Group"/>.</summary>
    public string DefaultGroup { get; set; } = "Dynamic";
}
