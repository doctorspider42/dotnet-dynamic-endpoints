using DynamicEndpoints;
using DynamicEndpoints.Audit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Chooses where audit entries go – see <c>AddAuditLog()</c>.</summary>
    public sealed class DynamicEndpointsAuditBuilder
    {
        internal DynamicEndpointsAuditBuilder(IServiceCollection services) => Services = services;

        public IServiceCollection Services { get; }

        internal bool HasSink { get; private set; }

        /// <summary>Writes entries to <c>ILogger</c> (category <c>DynamicEndpoints.Audit</c>) – the default when no sink is chosen.</summary>
        public DynamicEndpointsAuditBuilder ToLogger() => To<LoggerDynamicEndpointAuditSink>();

        /// <summary>Keeps the latest <paramref name="capacity"/> entries in memory, queryable through the admin API – for tests and development.</summary>
        public DynamicEndpointsAuditBuilder ToMemory(int capacity = 1000) => To(new InMemoryDynamicEndpointAuditLog(capacity));

        /// <summary>
        /// Writes entries to <typeparamref name="TSink"/>. When it implements <see cref="IDynamicEndpointAuditLog"/>, it is also what the
        /// admin API queries (the last one registered wins).
        /// </summary>
        public DynamicEndpointsAuditBuilder To<TSink>(ServiceLifetime lifetime = ServiceLifetime.Singleton)
            where TSink : class, IDynamicEndpointAuditSink
        {
            Services.TryAdd(new ServiceDescriptor(typeof(TSink), typeof(TSink), lifetime));
            return Register(typeof(TSink), sp => sp.GetRequiredService<TSink>(), lifetime);
        }

        public DynamicEndpointsAuditBuilder To(IDynamicEndpointAuditSink sink)
        {
            ArgumentNullException.ThrowIfNull(sink);
            return Register(sink.GetType(), _ => sink, ServiceLifetime.Singleton);
        }

        /// <summary>Who made a change, what to keep – see <see cref="DynamicEndpointsAuditOptions"/>.</summary>
        public DynamicEndpointsAuditBuilder Configure(Action<DynamicEndpointsAuditOptions> configure)
        {
            Services.Configure(configure);
            return this;
        }

        private DynamicEndpointsAuditBuilder Register(Type type, Func<IServiceProvider, IDynamicEndpointAuditSink> factory, ServiceLifetime lifetime)
        {
            HasSink = true;
            Services.Add(new ServiceDescriptor(typeof(IDynamicEndpointAuditSink), factory, lifetime));
            if (typeof(IDynamicEndpointAuditLog).IsAssignableFrom(type))
            {
                Services.Replace(new ServiceDescriptor(typeof(IDynamicEndpointAuditLog), sp => (IDynamicEndpointAuditLog)factory(sp), lifetime));
            }

            return this;
        }
    }

    public static class DynamicEndpointsAuditServiceCollectionExtensions
    {
        /// <summary>
        /// Records every change made through this instance's manager: who (the user of the HTTP request), what, when, and a
        /// property-by-property diff. Entries go to <c>ILogger</c> unless <paramref name="configure"/> chooses sinks
        /// (<c>a.ToMemory()</c>, <c>a.To&lt;MySink&gt;()</c>, <c>a.ToEntityFramework&lt;AppDbContext&gt;()</c>). Built on change events
        /// (<see cref="DynamicEndpointChangeOrigin.Local"/>), so each change is audited once, on the instance that made it.
        /// </summary>
        public static IDynamicEndpointsBuilder AddAuditLog(this IDynamicEndpointsBuilder builder, Action<DynamicEndpointsAuditBuilder>? configure = null)
        {
            builder.Services.AddOptions<DynamicEndpointsAuditOptions>();
            builder.Services.AddHttpContextAccessor();
            builder.AddChangeHandler<DynamicEndpointAuditHandler>();

            var audit = new DynamicEndpointsAuditBuilder(builder.Services);
            configure?.Invoke(audit);
            if (!audit.HasSink && !builder.Services.Any(s => s.ServiceType == typeof(IDynamicEndpointAuditSink)))
            {
                audit.ToLogger();
            }

            return builder;
        }
    }
}

namespace DynamicEndpoints.Audit
{
    using Microsoft.Extensions.DependencyInjection;

    /// <summary>The audit endpoints of the admin API.</summary>
    internal static class DynamicEndpointsAuditApi
    {
        public static void Map(RouteGroupBuilder group)
        {
            group.MapGet("/audit", (HttpContext http, Guid? endpointId, string? tenant, string? user, DateTimeOffset? from, DateTimeOffset? to, int? limit, CancellationToken ct) =>
                    QueryAsync(http, new DynamicEndpointAuditQuery { EndpointId = endpointId, Tenant = tenant, User = user, From = from, To = to, Limit = limit ?? 100 }, ct))
                .WithSummary("Queries the audit log (newest first) – needs a queryable audit sink.");

            group.MapGet("/{id:guid}/audit", (Guid id, HttpContext http, int? limit, CancellationToken ct) =>
                    QueryAsync(http, new DynamicEndpointAuditQuery { EndpointId = id, Limit = limit ?? 100 }, ct))
                .WithSummary("Audit log of a single endpoint (newest first).");
        }

        private static async Task<IResult> QueryAsync(HttpContext http, DynamicEndpointAuditQuery query, CancellationToken ct)
        {
            if (http.RequestServices.GetService<IDynamicEndpointAuditLog>() is not { } log)
            {
                return TypedResults.Problem("No queryable audit log is configured (AddAuditLog(a => a.ToMemory()) or a persistent sink).",
                    statusCode: StatusCodes.Status404NotFound);
            }

            // A tenant's admin API only sees its own entries.
            if (http.Items.TryGetValue(DynamicEndpointsEndpointRouteBuilderExtensions.AdminTenantKey, out var tenant) && tenant is string scoped)
            {
                query = query with { Tenant = scoped };
            }

            return TypedResults.Ok(await log.QueryAsync(query with { Limit = Math.Clamp(query.Limit, 1, 1000) }, ct));
        }
    }
}
