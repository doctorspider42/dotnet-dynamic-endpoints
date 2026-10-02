using DynamicEndpoints;
using DynamicEndpoints.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

public static class DynamicEndpointsEntityFrameworkCrudExtensions
{
    /// <summary>
    /// Registers the <c>ef-crud</c> processor on <typeparamref name="TContext"/> (registered by you with <c>AddDbContext</c>) with the
    /// allowlist of entities and fields admins may use:
    /// <code>
    /// .AddEntityFrameworkCrud&lt;AppDbContext&gt;(crud =&gt; crud
    ///     .Entity&lt;Product&gt;(e =&gt; e.Fields(p =&gt; p.Sku, p =&gt; p.Name, p =&gt; p.Price).Filterable(p =&gt; p.Name).Sortable(p =&gt; p.Name)))
    /// </code>
    /// Also adds <c>GET /crud/entities</c> and <c>POST /scaffold/crud</c> to the admin API and the <c>ef-crud</c> responses to the
    /// OpenAPI document. The configuration is checked against the EF model when the application starts. <paramref name="name"/> is
    /// the processor name, <c>ef-crud</c> by default – give each context its own when you expose more than one.
    /// </summary>
    public static IDynamicEndpointsBuilder AddEntityFrameworkCrud<TContext>(
        this IDynamicEndpointsBuilder builder,
        Action<DynamicCrudBuilder<TContext>> configure,
        string? name = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(configure);
        var processorName = name ?? DynamicCrud.ProcessorName;
        var registrations = builder.Services.Where(d => d.ServiceType == typeof(DynamicCrudRegistration))
            .Select(d => d.ImplementationInstance).OfType<DynamicCrudRegistration>().ToList();
        if (registrations.Any(r => r.ContextType == typeof(TContext)))
        {
            throw new InvalidOperationException($"AddEntityFrameworkCrud<{typeof(TContext).Name}> is called more than once – expose all its entities in one call.");
        }

        var crud = new DynamicCrudBuilder<TContext>();
        configure(crud);
        foreach (var entity in crud.Entities)
        {
            if (registrations.SelectMany(r => r.Entities).Any(e => string.Equals(e.Name, entity.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"The entity name '{entity.Name}' is already used by another AddEntityFrameworkCrud call.");
            }

            DynamicCrudNames.Register(entity.ClrType, entity.Name);
        }

        builder.Services.AddSingleton(new DynamicCrudRegistration(typeof(TContext), processorName, crud.Entities));
        builder.Services.TryAddSingleton<DynamicCrudCatalog>();
        builder.Services.TryAddSingleton<IDynamicCrudScaffolder, DynamicCrudScaffolder>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IDynamicEndpointsAdminApiExtension, DynamicCrudAdminApi>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<DynamicEndpointsOptions>, DynamicCrudOpenApi>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, DynamicCrudStartupCheck>());
        return builder.AddProcessor(typeof(DynamicCrudProcessor<TContext>), processorName, description: null);
    }
}
