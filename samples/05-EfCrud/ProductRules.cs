using DynamicEndpoints.EntityFrameworkCore;

namespace DynamicEndpoints.Samples.EfCrud;

/// <summary>
/// Application code around the writes of every ef-crud endpoint on products – whoever built the endpoint. Defaults and
/// server-side values (any property, exposed or not), business checks (<see cref="DynamicCrudContext{T}.Reject"/> → 400,
/// nothing saved), side effects after saving. Registered in DI as <c>IDynamicCrudInterceptor&lt;Product&gt;</c>.
/// </summary>
public sealed class ProductRules(ILogger<ProductRules> logger) : IDynamicCrudInterceptor<Product>
{
    public ValueTask BeforeCreateAsync(DynamicCrudContext<Product> context)
    {
        context.Entity.CreatedAt = DateTimeOffset.UtcNow;
        return Check(context);
    }

    public ValueTask BeforeUpdateAsync(DynamicCrudContext<Product> context) => Check(context);   // update and patch

    public ValueTask AfterCreateAsync(DynamicCrudContext<Product> context)
    {
        logger.LogInformation("Product {Sku} created through {Endpoint}.", context.Entity.Sku, context.Request.Endpoint.Name);
        return ValueTask.CompletedTask;
    }

    private static ValueTask Check(DynamicCrudContext<Product> context)
    {
        if (context.Entity.Price < 0)
        {
            context.Reject("price", "The price can't be negative.");
        }

        if (context.Entity.Stock < 0)
        {
            context.Reject("stock", "The stock can't be negative.");
        }

        return ValueTask.CompletedTask;
    }
}
