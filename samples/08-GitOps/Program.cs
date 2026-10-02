// 08 – GitOps: definitions in Git, reviewed in pull requests, pushed from CI. Export and import in a stable format (JSON or
// YAML), dry runs with a diff per endpoint, and the dynamic-endpoints CLI of this repository against the running app
// (gitops.ps1 / gitops.sh, endpoints.yaml).
//
//   dotnet run --project samples/08-GitOps      → http://localhost:5108/admin/
//   ./samples/08-GitOps/gitops.ps1   or   ./samples/08-GitOps/gitops.sh      (with the app running)

using DynamicEndpoints;
using DynamicEndpoints.Samples.GitOps;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(builder.Configuration.GetConnectionString("Default")));

builder.Services
    .AddDynamicEndpoints(options =>
    {
        options.ReservedPrefixes.Add("/swagger");
        options.ReservedPrefixes.Add("/openapi");
        options.OpenApi.Title = "GitOps – dynamic endpoints";
    })
    .AddFromAssemblyContaining<Program>()
    .AddResponseTemplateProcessor()
    // Export and import speak YAML too: ?format=yaml, Content-Type: application/yaml, and in the panel. (The CLI converts
    // YAML files locally, so it would work without this.)
    .AddYamlFormat()
    .UseEntityFrameworkStore<AppDbContext>();

builder.Services.AddOpenApi();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync(); // migrations in real life
}

app.MapDynamicEndpoints();

// The admin API for the panel – open here, .RequireAuthorization("admin") in real life.
app.MapDynamicEndpointsAdmin("/api/admin/endpoints");
app.MapDynamicEndpointsAdminUI("/admin", adminApiPath: "/api/admin/endpoints", options =>
{
    options.Title = "GitOps";
    options.SwaggerUrl = "/swagger";
    options.OpenApiUrl = "/openapi/dynamic.json";
});

// The same admin API for CI, behind an API key – it's a RouteGroupBuilder, so secure it like any other group (here a simple
// filter; in real life an authentication scheme and a policy). The CLI sends the key as X-Api-Key (--api-key or
// DYNAMIC_ENDPOINTS_API_KEY).
var ciApiKey = app.Configuration["Ci:ApiKey"];
app.MapDynamicEndpointsAdmin("/api/ci/endpoints").AddEndpointFilter(async (context, next) =>
    !string.IsNullOrEmpty(ciApiKey) && context.HttpContext.Request.Headers["X-Api-Key"] == ciApiKey
        ? await next(context)
        : Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "X-Api-Key is missing or wrong."));

app.MapDynamicEndpointsOpenApi("/openapi/dynamic.json");
app.MapOpenApi("/openapi/{documentName}.json");
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/openapi/dynamic.json", "Dynamic endpoints");
    options.SwaggerEndpoint("/openapi/v1.json", "Admin API");
});
app.MapGet("/", () => Results.Redirect("/admin/")).ExcludeFromDescription();

app.Run();

public partial class Program;
