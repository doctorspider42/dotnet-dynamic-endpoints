using DynamicEndpoints.EntityFrameworkCore;

namespace DynamicEndpoints.Samples.Showcase.Data;

/// <summary>
/// A product of a tenant's shop, exposed to admins through the <c>ef-crud</c> processor (see Program.cs): they can build list, get,
/// create and update endpoints on it, or scaffold them – but only with the fields allowlisted there, and only on their tenant's rows.
/// </summary>
public sealed class Product
{
    public int Id { get; set; }

    public string Sku { get; set; } = "";

    public string Name { get; set; } = "";

    public decimal Price { get; set; }

    public int Stock { get; set; }

    /// <summary>Set by <see cref="ProductRules"/>, read-only for requests.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The tenant column: every query of an ef-crud endpoint is filtered by it, new rows get the request's tenant.</summary>
    public string TenantId { get; set; } = "";

    /// <summary>Not allowlisted – never returned, never written by a dynamic endpoint.</summary>
    public decimal? PurchasePrice { get; set; }

    /// <summary>Concurrency token: the ETag of the ef-crud endpoints, checked against If-Match.</summary>
    public Guid Version { get; set; }
}

/// <summary>Server-side rules around the writes of ef-crud endpoints on products (registered in DI).</summary>
public sealed class ProductRules : IDynamicCrudInterceptor<Product>
{
    public ValueTask BeforeCreateAsync(DynamicCrudContext<Product> context)
    {
        context.Entity.CreatedAt = DateTimeOffset.UtcNow;
        return Check(context);
    }

    public ValueTask BeforeUpdateAsync(DynamicCrudContext<Product> context) => Check(context);

    private static ValueTask Check(DynamicCrudContext<Product> context)
    {
        if (context.Entity.Price < 0)
        {
            context.Reject("price", "The price can't be negative.");
        }

        return ValueTask.CompletedTask;
    }
}
