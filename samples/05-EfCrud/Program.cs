// 05 – CRUD on EF Core entities (ef-crud), the quick path without tenancy: you allowlist entities and fields, admins build –
// or scaffold – list, get, create, update, patch and delete endpoints on them. Paging, safe filters and sorting, ETags with
// If-Match, and an interceptor for the server-side rules.
//
//   dotnet run --project samples/05-EfCrud      → http://localhost:5105/admin/

using DynamicEndpoints;
using DynamicEndpoints.EntityFrameworkCore;
using DynamicEndpoints.Samples.EfCrud;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(builder.Configuration.GetConnectionString("Default")));

builder.Services
    .AddDynamicEndpoints(options =>
    {
        options.ReservedPrefixes.Add("/swagger");
        options.ReservedPrefixes.Add("/openapi");
        options.OpenApi.Title = "ef-crud – dynamic endpoints";
    })
    .AddFromAssemblyContaining<Program>()
    .UseEntityFrameworkStore<AppDbContext>()
    // The allowlist: nothing of the context is reachable unless it's listed here. Definitions store the entity's name, never
    // its CLR type. The configuration is checked against the EF model on start – unknown fields fail the start.
    .AddEntityFrameworkCrud<AppDbContext>(crud => crud
        // "products" (the DbSet name), with an explicit list of fields – the safe choice for entities that may grow columns.
        .Entity<Product>(e => e
            .Fields(p => p.Id, p => p.Sku, p => p.Name, p => p.Price, p => p.Stock, p => p.CategoryId)   // read and write
            .ReadOnly(p => p.CreatedAt)                                                                   // read only
            .Filterable(p => p.Sku, p => p.Name, p => p.Price)
            .Sortable(p => p.Name, p => p.Price))
        // "categories": every mapped scalar property except the ones listed. New columns become public automatically!
        .Entity<Category>(e => e
            .AllFields(except: c => c.InternalNote)
            .Filterable(c => c.Name, c => c.Active)
            .Sortable(c => c.Name)
            .Operations(CrudOperations.Read)));   // list and get only
    // No UseMultiTenancy() and no TenantColumn: the rows are simply shared, requests need no tenant – see 06-MultiTenancy.

// Server-side rules around the writes of every product endpoint (ProductRules.cs).
builder.Services.AddScoped<IDynamicCrudInterceptor<Product>, ProductRules>();

builder.Services.AddOpenApi();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();   // migrations in real life
    await db.SeedDataAsync();
}

app.MapDynamicEndpoints();
// The panel shows the ef-crud form and the *Scaffold CRUD* wizard, because the admin API reports the "crud" feature.
app.MapDynamicEndpointsAdmin("/api/admin/endpoints");   // .RequireAuthorization("admin") in real life
app.MapDynamicEndpointsAdminUI("/admin", adminApiPath: "/api/admin/endpoints", options =>
{
    options.Title = "ef-crud";
    options.SwaggerUrl = "/swagger";
    options.OpenApiUrl = "/openapi/dynamic.json";
});
app.MapDynamicEndpointsOpenApi("/openapi/dynamic.json");   // response schemas generated from the live model
app.MapOpenApi("/openapi/{documentName}.json");
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/openapi/dynamic.json", "Dynamic endpoints");
    options.SwaggerEndpoint("/openapi/v1.json", "Admin API");
});
app.MapGet("/", () => Results.Redirect("/admin/")).ExcludeFromDescription();

app.Run();

public partial class Program;
