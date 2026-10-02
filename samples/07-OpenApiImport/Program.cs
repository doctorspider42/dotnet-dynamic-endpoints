// 07 – Import from OpenAPI: a contract-first API as runtime endpoints. petstore.json is imported in mock mode on every start
// (PetstoreImport.cs), petstore-v2.json shows a re-import with sync, and the panel's OpenAPI import wizard does the same by hand.
//
//   dotnet run --project samples/07-OpenApiImport      → http://localhost:5107/admin/

using DynamicEndpoints;
using DynamicEndpoints.Samples.OpenApiImport;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(builder.Configuration.GetConnectionString("Default")));

builder.Services
    .AddDynamicEndpoints(options =>
    {
        options.ReservedPrefixes.Add("/swagger");
        options.ReservedPrefixes.Add("/openapi");
        options.ReservedPrefixes.Add("/specs");
        options.OpenApi.Title = "OpenAPI import – dynamic endpoints";
    })
    // PetstoreImport, the seeder that imports petstore.json.
    .AddFromAssemblyContaining<Program>()
    // Mock mode answers with the built-in "response" processor – it has to be registered.
    .AddResponseTemplateProcessor()
    // A processor the document asks for by name (x-dynamic-endpoints-processor on GET /store/inventory).
    .AddProcessor("inventory", _ => Results.Ok(new { available = 7, pending = 2, sold = 12 }), "Pets per status (from the warehouse).")
    // An all-purpose processor for imports with a processor per tag (processorByTag=store:echo).
    .AddProcessor("echo", request => Results.Ok(new { endpoint = request.Endpoint.Name, parameters = request.Parameters }), "Echoes the parameters.")
    // Import YAML documents too (the panel, the admin API, the CLI's import-openapi).
    .AddYamlFormat()
    .UseEntityFrameworkStore<AppDbContext>();

builder.Services.AddOpenApi();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync(); // migrations in real life
}

app.MapDynamicEndpoints();
// POST /api/admin/endpoints/import/openapi – what the panel's wizard and the CLI's import-openapi call.
app.MapDynamicEndpointsAdmin("/api/admin/endpoints");   // .RequireAuthorization("admin") in real life
app.MapDynamicEndpointsAdminUI("/admin", adminApiPath: "/api/admin/endpoints", options =>
{
    options.Title = "OpenAPI import";
    options.SwaggerUrl = "/swagger";
    options.OpenApiUrl = "/openapi/dynamic.json";
});
// The two documents, served as they are – open one, copy it, paste it into the wizard.
foreach (var file in new[] { "petstore.json", "petstore-v2.json" })
{
    app.MapGet("/specs/" + file, (IWebHostEnvironment environment) =>
        Results.File(Path.Combine(environment.ContentRootPath, file), "application/json")).ExcludeFromDescription();
}

app.MapDynamicEndpointsOpenApi("/openapi/dynamic.json");   // the imported endpoints, documented again – from the definitions
app.MapOpenApi("/openapi/{documentName}.json");
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/openapi/dynamic.json", "Dynamic endpoints");
    options.SwaggerEndpoint("/openapi/v1.json", "Admin API");
});
app.MapGet("/", () => Results.Redirect("/admin/")).ExcludeFromDescription();

app.Run();

public partial class Program;
