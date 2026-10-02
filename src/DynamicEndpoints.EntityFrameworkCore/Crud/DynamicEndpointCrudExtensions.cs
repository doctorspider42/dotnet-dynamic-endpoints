using DynamicEndpoints.EntityFrameworkCore;

namespace DynamicEndpoints;

/// <summary>Typed selection of the <c>ef-crud</c> processor for <see cref="DynamicEndpoint"/>.</summary>
public static class DynamicEndpointCrudExtensions
{
    /// <summary>
    /// Handles the endpoint with <c>ef-crud</c> on <typeparamref name="TEntity"/>. The entity name is the one it was exposed under
    /// with <c>AddEntityFrameworkCrud</c> (or its <see cref="DynamicEntityAttribute"/>) – no magic strings in seeders:
    /// <code>DynamicEndpoint.Get("/products").HandledByCrud&lt;Product&gt;(CrudOperation.List, c =&gt; c.PageSize = 20)</code>
    /// Add the parameters yourself (route key, body fields), or generate whole endpoints with <see cref="IDynamicCrudScaffolder"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The type isn't exposed, or under several names.</exception>
    public static DynamicEndpoint HandledByCrud<TEntity>(
        this DynamicEndpoint endpoint,
        CrudOperation operation,
        Action<DynamicCrudConfig>? configure = null,
        string processor = DynamicCrud.ProcessorName)
        where TEntity : class =>
        endpoint.HandledByCrud(DynamicCrudNames.Of(typeof(TEntity)), operation, configure, processor);

    /// <summary>Handles the endpoint with <c>ef-crud</c> on the entity exposed as <paramref name="entity"/>.</summary>
    public static DynamicEndpoint HandledByCrud(
        this DynamicEndpoint endpoint,
        string entity,
        CrudOperation operation,
        Action<DynamicCrudConfig>? configure = null,
        string processor = DynamicCrud.ProcessorName)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        var config = new DynamicCrudConfig { Entity = entity, Operation = operation };
        configure?.Invoke(config);
        return endpoint.HandledBy(processor, config);
    }
}
