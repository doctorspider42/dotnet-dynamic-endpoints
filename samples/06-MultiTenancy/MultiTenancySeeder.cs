using System.Text.Json.Nodes;
using DynamicEndpoints.EntityFrameworkCore;
using DynamicEndpoints.Sql;

namespace DynamicEndpoints.Samples.MultiTenancy;

/// <summary>
/// Shared endpoints (no tenant – every tenant gets them) and tenant endpoints (only requests of that tenant), two tenants on the
/// same route with different configurations, ef-crud endpoints that serve each tenant its own rows, and sql-query per tenant.
/// </summary>
public sealed class MultiTenancySeeder(IDynamicEndpointManager manager, IDynamicCrudScaffolder scaffolder) : IDynamicEndpointSeeder
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if ((await manager.ListAsync(cancellationToken)).Count > 0)
        {
            return;
        }

        // Shared: routed for every tenant – and for requests without one.
        await manager.CreateAsync(DynamicEndpoint.Get("/status")
            .Named("Status (shared)")
            .InGroup("Shared")
            .HandledBy<ResponseTemplateProcessor, ResponseTemplateConfig>(new() { Body = new JsonObject { ["status"] = "ok" } }), cancellationToken);

        // Tenant endpoints through the manager's tenant view: it assigns the tenant and sees nothing else.
        var acme = manager.ForTenant("acme");
        await acme.CreateAsync(DynamicEndpoint.Get("/welcome")
            .Named("Welcome")
            .InGroup("Tenant endpoints")
            .HandledBy<ResponseTemplateProcessor, ResponseTemplateConfig>(new() { Body = new JsonObject { ["text"] = "Welcome to Acme, {{name}}!" } })
            .FromQuery("name", p => p.String().MaxLength(50).Default("guest")), cancellationToken);

        await acme.CreateAsync(DynamicEndpoint.Get("/promo")
            .Named("Promotion (acme only)")
            .InGroup("Tenant endpoints")
            .HandledBy<ResponseTemplateProcessor, ResponseTemplateConfig>(new() { Body = new JsonObject { ["code"] = "ROADRUNNER", ["discount"] = 15 } }), cancellationToken);

        // sql-query of a tenant: only the connections assigned to acme ("acme-reports", Program.cs) are allowed.
        await acme.CreateAsync(DynamicEndpoint.Get("/reports/stock")
            .Named("Stock report (acme, SQL)")
            .InGroup("Tenant endpoints")
            .HandledBy<SqlQueryProcessor, SqlQueryConfig>(new()
            {
                Connection = "acme-reports",
                // In real life acme's own reporting database; here the shared file, so the query filters by the tenant itself.
                Query = """SELECT "Sku" AS sku, "Stock" AS stock FROM "Products" WHERE "TenantId" = 'acme' ORDER BY "Sku" """,
            }), cancellationToken);

        // The same route for another tenant, with its own configuration – or set the tenant on the definition directly.
        await manager.CreateAsync(DynamicEndpoint.Get("/welcome")
            .Named("Welcome")
            .InGroup("Tenant endpoints")
            .HandledBy<ResponseTemplateProcessor, ResponseTemplateConfig>(new() { Body = new JsonObject { ["text"] = "Globex greets you, {{name}}." } })
            .FromQuery("name", p => p.String().MaxLength(50).Default("visitor"))
            .Build() with { Tenant = "globex" }, cancellationToken);

        // ef-crud, scaffolded as shared endpoints: one endpoint for every tenant, each tenant sees and writes its own rows
        // (the tenant column). Without a tenant the request is a 404 – never all rows.
        await scaffolder.ScaffoldAsync("products", new DynamicCrudScaffoldOptions
        {
            RoutePrefix = "/products",
            Group = "Products (ef-crud, per tenant)",
            Operations = CrudOperations.All & ~CrudOperations.Delete,
            Enabled = true,
        }, cancellationToken);

        // Reference data without a tenant column, shared by everybody (SharedAcrossTenants in Program.cs).
        await scaffolder.ScaffoldAsync("countries", new DynamicCrudScaffoldOptions
        {
            RoutePrefix = "/countries",
            Group = "Countries (ef-crud, shared)",
            Enabled = true,
        }, cancellationToken);
    }
}
