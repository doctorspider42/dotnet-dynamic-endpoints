using DynamicEndpoints;
using DynamicEndpoints.Tenancy;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class DynamicEndpointsTenancyServiceCollectionExtensions
    {
        /// <summary>
        /// Lets endpoints belong to tenants (<see cref="DynamicEndpointDefinition.Tenant"/>). Each request is resolved to a tenant by
        /// the resolvers configured in <paramref name="configure"/> (first match wins), and routing serves it the endpoints of its
        /// tenant plus the shared ones. Several tenants can use the same route.
        /// </summary>
        /// <example>
        /// <code>
        /// builder.Services.AddDynamicEndpoints().UseMultiTenancy(t =&gt; t.FromHeader("X-Tenant-Id").FromHost());
        /// </code>
        /// </example>
        public static IDynamicEndpointsBuilder UseMultiTenancy(this IDynamicEndpointsBuilder builder, Action<DynamicEndpointsTenancyBuilder> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            builder.Services.Configure<DynamicEndpointsOptions>(o => o.Tenancy.Enabled = true);
            builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<MatcherPolicy, DynamicEndpointTenantMatcherPolicy>());
            configure(new DynamicEndpointsTenancyBuilder(builder.Services));
            return builder;
        }
    }
}

namespace Microsoft.AspNetCore.Http
{
    public static class DynamicEndpointsTenantHttpContextExtensions
    {
        /// <summary>
        /// The tenant of this request as found by the resolvers of <c>UseMultiTenancy()</c> (resolved once per request), or <c>null</c>.
        /// </summary>
        public static ValueTask<string?> GetDynamicEndpointTenantAsync(this HttpContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return context.RequestServices.GetService<DynamicEndpointTenantResolution>()?.ResolveAsync(context) ?? ValueTask.FromResult<string?>(null);
        }
    }
}
