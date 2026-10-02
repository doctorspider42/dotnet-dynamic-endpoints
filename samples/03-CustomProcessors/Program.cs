// 03 – Custom processors: typed processors with configuration, DI and database access, an inline processor, a filter, forms and
// file uploads, handing work from a validator to the processor, and a management API of your own on IDynamicEndpointManager.
//
//   dotnet run --project samples/03-CustomProcessors      → http://localhost:5103/admin/

using DynamicEndpoints;
using DynamicEndpoints.Samples.CustomProcessors;
using DynamicEndpoints.Samples.CustomProcessors.Greetings;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(builder.Configuration.GetConnectionString("Default")));

builder.Services
    .AddDynamicEndpoints(options =>
    {
        options.ReservedPrefixes.Add("/swagger");
        options.ReservedPrefixes.Add("/openapi");
        options.ReservedPrefixes.Add("/api");   // the custom management API (/api/greetings) stays static
        options.OpenApi.Title = "Custom processors – dynamic endpoints";
    })
    // Processors/ (template, calculator, collection, file-info, csv-summary), Validators/ (csv) and the seeder, in one call.
    .AddFromAssemblyContaining<Program>()
    // An inline processor – handy for small things. Typed classes are easier to test and get a configuration.
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
    // Filters aren't scanned – they run for every dynamic endpoint, so you add them on purpose.
    .AddFilter<ApiKeyFilter>()
    .UseEntityFrameworkStore<AppDbContext>();

// The service behind the custom management API (Greetings/): it creates endpoints through the injected manager.
builder.Services.AddScoped<GreetingEndpointsService>();

builder.Services.AddOpenApi();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync(); // migrations in real life
}

app.MapDynamicEndpoints();

// The generic admin API and panel – and next to them a purpose-built one: POST /api/greetings { "slug", "greeting" }.
app.MapDynamicEndpointsAdmin("/api/admin/endpoints");   // .RequireAuthorization("admin") in real life
app.MapDynamicEndpointsAdminUI("/admin", adminApiPath: "/api/admin/endpoints", options =>
{
    options.Title = "Custom processors";
    options.SwaggerUrl = "/swagger";
    options.OpenApiUrl = "/openapi/dynamic.json";
});
app.MapGreetingsApi();

app.MapDynamicEndpointsOpenApi("/openapi/dynamic.json");
app.MapOpenApi("/openapi/{documentName}.json");
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/openapi/dynamic.json", "Dynamic endpoints");
    options.SwaggerEndpoint("/openapi/v1.json", "Admin API and greetings API");
});
app.MapGet("/", () => Results.Redirect("/admin/")).ExcludeFromDescription();

app.Run();

public partial class Program;
