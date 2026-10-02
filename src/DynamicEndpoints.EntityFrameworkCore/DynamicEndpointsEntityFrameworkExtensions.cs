using DynamicEndpoints;
using DynamicEndpoints.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

public static class DynamicEndpointsEntityFrameworkExtensions
{
    /// <summary>
    /// Persists definitions through your own context. Add the table to its model with
    /// <c>modelBuilder.ApplyDynamicEndpointsConfiguration()</c> and create it via your migrations.
    /// </summary>
    public static IDynamicEndpointsBuilder UseEntityFrameworkStore<TContext>(this IDynamicEndpointsBuilder builder)
        where TContext : DbContext
    {
        builder.Services.Replace(ServiceDescriptor.Scoped<IDynamicEndpointStore>(sp =>
            new EntityFrameworkDynamicEndpointStore<TContext>(sp.GetRequiredService<TContext>(), saveChanges: true)));
        return builder;
    }

    /// <summary>
    /// Persists definitions through the bundled <see cref="DynamicEndpointsDbContext"/>. With <paramref name="migrateOnStartup"/>
    /// its migrations are applied before definitions are loaded – a table created earlier with <c>EnsureCreated</c> is adopted.
    /// </summary>
    public static IDynamicEndpointsBuilder UseEntityFrameworkStore(
        this IDynamicEndpointsBuilder builder,
        Action<DbContextOptionsBuilder> configure,
        bool migrateOnStartup = false)
    {
        builder.Services.AddDbContext<DynamicEndpointsDbContext>(configure);
        builder.UseEntityFrameworkStore<DynamicEndpointsDbContext>();
        return migrateOnStartup ? builder.MigrateOnStartup<DynamicEndpointsDbContext>() : builder;
    }

    /// <summary>
    /// Applies the migrations of <typeparamref name="TContext"/> on start-up, before definitions are loaded. Convenient for
    /// small deployments – with several instances prefer a migration step in your deployment pipeline.
    /// </summary>
    public static IDynamicEndpointsBuilder MigrateOnStartup<TContext>(this IDynamicEndpointsBuilder builder)
        where TContext : DbContext =>
        builder.AddStoreInitializer<MigrateDatabaseInitializer<TContext>>();
}
