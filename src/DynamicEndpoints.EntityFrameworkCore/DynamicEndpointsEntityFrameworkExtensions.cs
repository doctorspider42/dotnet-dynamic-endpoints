using DynamicEndpoints.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Microsoft.Extensions.DependencyInjection;

public static class DynamicEndpointsEntityFrameworkExtensions
{
    /// <summary>
    /// Persists definitions through your own context. Add the table to its model with
    /// <c>modelBuilder.ApplyDynamicEndpointsConfiguration()</c> and create it via migrations.
    /// </summary>
    public static IDynamicEndpointsBuilder UseEntityFrameworkStore<TContext>(this IDynamicEndpointsBuilder builder)
        where TContext : DbContext =>
        builder.UseStore<EntityFrameworkDynamicEndpointStore<TContext>>();

    /// <summary>Persists definitions through the bundled <see cref="DynamicEndpointsDbContext"/>.</summary>
    public static IDynamicEndpointsBuilder UseEntityFrameworkStore(this IDynamicEndpointsBuilder builder, Action<DbContextOptionsBuilder> configure)
    {
        builder.Services.AddDbContext<DynamicEndpointsDbContext>(configure);
        return builder.UseEntityFrameworkStore<DynamicEndpointsDbContext>();
    }
}
