using DynamicEndpoints;
using DynamicEndpoints.EntityFrameworkCore;
using DynamicEndpoints.Samples.Showcase;
using DynamicEndpoints.Samples.Showcase.Data;
using DynamicEndpoints.Samples.Showcase.Demo;
using DynamicEndpoints.Samples.Showcase.Greetings;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// OpenTelemetry with the per-endpoint metrics and traces, health checks, service discovery (DynamicEndpoints.ServiceDefaults).
builder.AddServiceDefaults();
var demo = builder.AddDemoMode();

// SQLite by default; PostgreSQL when there's a "dynamicendpoints" connection string (the Aspire AppHost, docker compose).
var postgres = builder.Configuration.GetConnectionString("dynamicendpoints");
builder.Services.AddDbContext<AppDbContext>(options =>
{
    if (postgres is not null)
    {
        options.UseNpgsql(postgres);
    }
    else
    {
        options.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=dynamic-endpoints.db");
    }
});

var dynamicEndpoints = builder.Services
    .AddDynamicEndpoints(options =>
    {
        options.ReservedPrefixes.Add("/admin");
        options.ReservedPrefixes.Add("/swagger");
        options.ReservedPrefixes.Add("/openapi");
        options.ReservedPrefixes.Add("/api");
        options.OpenApi.Title = "Sample dynamic API";
        options.OpenApi.Description = "Endpoints defined at runtime in the admin panel.";
        // Picks up changes made by other instances sharing the database.
        options.RefreshInterval = TimeSpan.FromSeconds(30);
    })
    // All processors, validators and seeders of this project – EchoProcessor, NipValidator, SampleEndpointsSeeder, …
    .AddFromAssemblyContaining<Program>()
    // All FluentValidation validators – BookingRequestValidator -> "booking-request", IbanValidator -> "iban"
    .AddFluentValidatorsFromAssemblyContaining<Program>()
    .AddProcessor("clock", request =>
    {
        var zoneId = request.Get<string>("timeZone") ?? "UTC";
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var zone))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["timeZone"] = [$"Unknown time zone '{zoneId}'."] });
        }

        var now = DateTimeOffset.UtcNow;
        return Results.Ok(new { timeZone = zone.Id, utc = now, local = TimeZoneInfo.ConvertTime(now, zone) });
    }, "Returns the current time in the 'timeZone' parameter (IANA or Windows id).")
    // http-forward, webhook and response – configured with forms in the panel.
    .AddBuiltInProcessors()
    // sql-query on the sample's own database (read-only statements, always rolled back). Use a read-only database user in real life.
    .AddSqlQueryProcessor(services =>
    {
        var connectionString = services.GetRequiredService<AppDbContext>().Database.GetConnectionString();
        return postgres is not null ? new NpgsqlConnection(connectionString) : new SqliteConnection(connectionString);
    })
    // Export and import as YAML too (admin API ?format=yaml, the panel, the CLI).
    .AddYamlFormat()
    // Endpoints can belong to a tenant, picked by the X-Tenant header; endpoints without one are shared by all tenants.
    .UseMultiTenancy(tenancy => tenancy.FromHeader("X-Tenant"))
    .UseEntityFrameworkStore<AppDbContext>()
    // ef-crud: admins build CRUD endpoints on the entities and fields allowlisted here – or scaffold them in the panel.
    // Products have a tenant column, so every endpoint only sees the rows of its (or the request's) tenant.
    .AddEntityFrameworkCrud<AppDbContext>(crud => crud
        .Entity<Product>(e => e                                   // "products" – the DbSet name
            .Fields(p => p.Id, p => p.Sku, p => p.Name, p => p.Price, p => p.Stock)
            .ReadOnly(p => p.CreatedAt)
            .Filterable(p => p.Sku, p => p.Name, p => p.Price)
            .Sortable(p => p.Name, p => p.Price)
            .TenantColumn(p => p.TenantId)))
    // Who changed which endpoint and how – logged, and the latest entries at GET /api/admin/endpoints/audit.
    .AddAuditLog(audit => audit.ToLogger().ToMemory());

// Rate limits and output caching defined in endpoints need the ASP.NET Core middleware.
builder.Services.AddRateLimiter(options => options.RejectionStatusCode = StatusCodes.Status429TooManyRequests);
builder.Services.AddOutputCache();

// Several instances: Redis tells the others about a change right away (polling above stays the fallback).
if (builder.Configuration.GetConnectionString("redis") is { Length: > 0 } redis)
{
    dynamicEndpoints.UseRedisChangeNotifications(redis);
}

// Server-side rules of the ef-crud product endpoints: CreatedAt, no negative prices.
builder.Services.AddScoped<IDynamicCrudInterceptor<Product>, ProductRules>();

// Own feature service that manages dynamic endpoints through the injected IDynamicEndpointManager.
builder.Services.AddScoped<GreetingEndpointsService>();

// OpenAPI document of the static (admin) API – the dynamic one is generated by the library.
builder.Services.AddOpenApi();

var app = builder.Build();

// Demo only – use migrations in a real application.
await SampleDatabase.EnsureCreatedAsync(app.Services);

// The endpoints' own rate limits, plus the demo's global limit per client.
app.UseRateLimiter();
app.UseOutputCache();

app.MapGet("/", () => Results.Redirect("/admin/")).ExcludeFromDescription();
app.MapDefaultEndpoints();

var title = demo.Enabled ? $"Dynamic Endpoints – live demo, resets every {demo.ResetInterval.TotalMinutes:0} min" : "Dynamic Endpoints";
app.MapDynamicEndpoints();
app.MapDynamicEndpointsAdmin("/api/admin/endpoints"); // generic admin API – .RequireAuthorization("admin") in real life
app.MapDynamicEndpointsAdminUI("/admin", adminApiPath: "/api/admin/endpoints", o =>  // the panel (DynamicEndpoints.AdminUI)
{
    o.Title = title;
    o.SwaggerUrl = "/swagger";
    o.OpenApiUrl = "/openapi/dynamic.json";
    o.TenantOpenApiUrl = "/openapi/{tenant}/dynamic.json"; // follows the tenant picked in the panel
});
// A tenant's own admin API and panel – /admin/tenants/acme/ manages only the endpoints of "acme".
// In real life: .RequireAuthorization() with a policy that checks the user belongs to the tenant.
app.MapDynamicEndpointsTenantAdmin("/api/admin/tenants/{tenant}/endpoints");
app.MapDynamicEndpointsAdminUI("/admin/tenants/{tenant}", adminApiPath: "/api/admin/tenants/{tenant}/endpoints", o =>
{
    o.Title = $"{title} – tenant";
    o.OpenApiUrl = "/openapi/{tenant}/dynamic.json";
});
app.MapGreetingsApi();                                  // purpose-built API using the injected manager
app.MapDynamicEndpointsOpenApi("/openapi/dynamic.json");
app.MapDynamicEndpointsOpenApi("/openapi/{tenant}/dynamic.json");
app.MapOpenApi("/openapi/{documentName}.json");

app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/openapi/dynamic.json", "Dynamic endpoints");
    options.SwaggerEndpoint("/openapi/v1.json", "Admin API");
    options.DocumentTitle = "Dynamic endpoints – Swagger UI";
});

app.Run();

public partial class Program;
