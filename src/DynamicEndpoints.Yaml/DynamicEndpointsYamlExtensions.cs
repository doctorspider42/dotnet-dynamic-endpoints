using DynamicEndpoints;
using DynamicEndpoints.Yaml;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

public static class DynamicEndpointsYamlExtensions
{
    /// <summary>
    /// Lets the admin API read and write YAML: <c>GET /export?format=yaml</c>, and <c>POST /import</c> or <c>/import/openapi</c>
    /// with <c>Content-Type: application/yaml</c>.
    /// </summary>
    public static IDynamicEndpointsBuilder AddYamlFormat(this IDynamicEndpointsBuilder builder)
    {
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IDynamicEndpointsTextFormat, YamlDynamicEndpointsTextFormat>());
        return builder;
    }
}
