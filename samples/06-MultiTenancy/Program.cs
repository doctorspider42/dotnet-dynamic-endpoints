// 06 – Multi-tenancy: the tenant comes from the X-Tenant header. Endpoints of a tenant are only routed for that tenant, shared
// ones for all; tenants can use the same route. Each tenant gets its own admin API and panel, its own OpenAPI document, its own
// rows in ef-crud endpoints, and only its own SQL connections.
//
//   dotnet run --project samples/06-MultiTenancy      → http://localhost:5106/admin/  ·  acme's panel: /admin/tenants/acme/

using DynamicEndpoints;
using DynamicEndpoints.EntityFrameworkCore;
using DynamicEndpoints.Samples.MultiTenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default");
var readOnly = new SqliteConnectionStringBuilder(connectionString) { Mode = SqliteOpenMode.ReadOnly }.ToString();
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));

builder.Services
    .AddDynamicEndpoints(options =>
    {
        options.ReservedPrefixes.Add("/swagger");
        options.ReservedPrefixes.Add("/openapi");
        options.OpenApi.Title = "Multi-tenancy – dynamic endpoints";
        // So Swagger UI and generated clients send the tenant: required for the tenants' own endpoints. (Shared ef-crud
        // endpoints on an entity with a tenant column get it from the library; other shared endpoints don't need it.)
        options.OpenApi.AddHeader("X-Tenant", "The tenant: acme or globex.", required: true, appliesTo: d => d.Tenant is not null);
    })
    .AddFromAssemblyContaining<Program>()
    // 1. How a request finds its tenant. Others: FromHost(), FromClaim("tenant_id"), FromRoutePrefix("/t/{tenant}"), your own.
    //    Resolvers run in order, the first with a result wins, and only for routes that have tenant endpoints.
    .UseMultiTenancy(tenancy => tenancy.FromHeader("X-Tenant"))
    .UseEntityFrameworkStore<AppDbContext>()
    .AddResponseTemplateProcessor()
    // 2. sql-query: shared endpoints may use every connection, a tenant's endpoints only the ones assigned to it. The default
    //    connection ("") is the application's – no tenant may use it.
    .AddSqlQueryProcessor(_ => new SqliteConnection(readOnly), sql =>
    {
        sql.Connections["acme-reports"] = _ => new SqliteConnection(readOnly);   // in real life: acme's own reporting database
        sql.AllowTenants("acme-reports", "acme");
    })
    // 3. ef-crud with a tenant column: every query is filtered by the tenant, new rows get it. Entities without a tenant column
    //    are for shared endpoints only – unless SharedAcrossTenants() (or AllowTenants(…)) says otherwise.
    .AddEntityFrameworkCrud<AppDbContext>(crud => crud
        .Entity<Product>(e => e
            .Fields(p => p.Id, p => p.Sku, p => p.Name, p => p.Price, p => p.Stock)
            .Filterable(p => p.Sku, p => p.Price)
            .Sortable(p => p.Name, p => p.Price)
            .TenantColumn(p => p.TenantId))
        .Entity<Country>(e => e
            .AllFields()
            .Operations(CrudOperations.Read)
            .SharedAcrossTenants()));

builder.Services.AddOpenApi();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();   // migrations in real life
    await db.SeedDataAsync();
}

app.MapDynamicEndpoints();

// 4. The global admin API and panel manage every tenant: a tenant filter, a tenant field in the editor, a Try console that sends
//    X-Tenant. Its OpenAPI link follows the tenant picked in the panel.
app.MapDynamicEndpointsAdmin("/api/admin/endpoints");   // .RequireAuthorization("admin") in real life
app.MapDynamicEndpointsAdminUI("/admin", adminApiPath: "/api/admin/endpoints", options =>
{
    options.Title = "Multi-tenancy – all tenants";
    options.SwaggerUrl = "/swagger";
    options.OpenApiUrl = "/openapi/dynamic.json";
    options.TenantOpenApiUrl = "/openapi/{tenant}/dynamic.json";
});

// 5. A tenant's own admin API and panel: /admin/tenants/acme/ sees, creates, drafts, exports and imports only acme's endpoints.
//    In real life: .RequireAuthorization() with a policy that checks the user belongs to the tenant in the route – on both.
app.MapDynamicEndpointsTenantAdmin("/api/admin/tenants/{tenant}/endpoints");
app.MapDynamicEndpointsAdminUI("/admin/tenants/{tenant}", adminApiPath: "/api/admin/tenants/{tenant}/endpoints", options =>
{
    options.Title = "Multi-tenancy – tenant";
    options.OpenApiUrl = "/openapi/{tenant}/dynamic.json";
});

// 6. OpenAPI per tenant (shared + the tenant's endpoints) – tenants may share a path, so one document can't hold them all.
//    /openapi/dynamic.json is the request's tenant (X-Tenant), or the shared endpoints only.
app.MapDynamicEndpointsOpenApi("/openapi/dynamic.json");
app.MapDynamicEndpointsOpenApi("/openapi/{tenant}/dynamic.json");
app.MapOpenApi("/openapi/{documentName}.json");
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/openapi/acme/dynamic.json", "Tenant acme");
    options.SwaggerEndpoint("/openapi/globex/dynamic.json", "Tenant globex");
    options.SwaggerEndpoint("/openapi/dynamic.json", "Shared endpoints");
    options.SwaggerEndpoint("/openapi/v1.json", "Admin API");
});
app.MapGet("/", () => Results.Redirect("/admin/")).ExcludeFromDescription();

app.Run();

public partial class Program;
