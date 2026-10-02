using DynamicEndpoints;

namespace Microsoft.AspNetCore.Http;

public static class DynamicEndpointsHttpContextExtensions
{
    /// <summary>
    /// The dynamic endpoint this request was routed to – with its complete definition, so middleware never needs to query
    /// the store – or <c>null</c> when it is no dynamic endpoint (or routing did not run yet).
    /// </summary>
    public static DynamicEndpointMetadata? GetDynamicEndpoint(this HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<DynamicEndpointMetadata>();
}
