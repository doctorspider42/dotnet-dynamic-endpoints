using DynamicEndpoints.EntityFrameworkCore;

namespace DynamicEndpoints.Samples.EfCrud;

/// <summary>
/// Two ways to get ef-crud endpoints: scaffolded from the EF model (like the panel's *Scaffold CRUD* wizard), or written by hand
/// with <c>HandledByCrud&lt;T&gt;</c>. Either way they are ordinary definitions – change them in the panel afterwards.
/// </summary>
public sealed class EfCrudSeeder(IDynamicEndpointManager manager, IDynamicCrudScaffolder scaffolder, ILogger<EfCrudSeeder> logger)
    : IDynamicEndpointSeeder
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if ((await manager.ListAsync(cancellationToken)).Count > 0)
        {
            return;
        }

        // 1. Scaffolded: list, get, create, update, patch and delete on products, with typed parameters, max lengths, the price
        //    range, paging, sorting and a filter parameter per filterable field (sku, name, price, minPrice, maxPrice).
        var scaffolded = await scaffolder.ScaffoldAsync("products", new DynamicCrudScaffoldOptions
        {
            RoutePrefix = "/shop/products",
            Group = "Products (scaffolded)",
            Enabled = true,   // scaffolded endpoints are disabled by default – review first
        }, cancellationToken);
        logger.LogInformation("Scaffolded {Count} product endpoints.", scaffolded.Created);

        // 2. By hand, with HandledByCrud<T>: the entity name comes from the registration, the parameters are yours.
        DynamicEndpointDefinition[] definitions =
        [
            DynamicEndpoint.Get("/shop/categories")
                .Named("List categories")
                .WithDescription("?q= searches the name, ?active= filters, sorted by name.")
                .InGroup("Categories (HandledByCrud)")
                .HandledByCrud<Category>(CrudOperation.List, c =>
                {
                    c.PageSize = 20;
                    c.Sort = "name";
                    c.Filters =
                    [
                        new() { Field = "name", Operator = CrudFilterOperator.Contains, Parameter = "q" },
                        new() { Field = "active", Operator = CrudFilterOperator.Eq, Parameter = "active" },
                    ];
                })
                .FromQuery("q", p => p.String().MaxLength(50))
                .FromQuery("active", p => p.Boolean())
                .FromQuery("page", p => p.Integer().Min(1))
                .FromQuery("pageSize", p => p.Integer().Range(1, 100)),

            DynamicEndpoint.Get("/shop/categories/{id}")
                .Named("Get category")
                .InGroup("Categories (HandledByCrud)")
                .HandledByCrud<Category>(CrudOperation.Get)
                .FromRoute("id", p => p.Integer()),

            // Optimistic concurrency made mandatory: without If-Match this answers 428, with a stale ETag 412.
            DynamicEndpoint.Patch("/shop/products/{id}/stock")
                .Named("Set stock (If-Match required)")
                .InGroup("Products (scaffolded)")
                .HandledByCrud<Product>(CrudOperation.Patch, c => c.RequireIfMatch = true)
                .FromRoute("id", p => p.Integer())
                .FromBody("stock", p => p.Integer().Required().Min(0)),
        ];

        foreach (var definition in definitions)
        {
            await manager.CreateAsync(definition, cancellationToken);
        }
    }
}
