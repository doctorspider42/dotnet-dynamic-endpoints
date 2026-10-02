// 04 – Built-in processors: http-forward, webhook, response and sql-query. Endpoints without a line of processor code – only
// configuration, with a form for each of them in the panel. The forward and webhook targets are fake services mapped in this
// app (FakeUpstream.cs), so everything works offline.
//
//   dotnet run --project samples/04-BuiltInProcessors      → http://localhost:5104/admin/

using DynamicEndpoints;
using DynamicEndpoints.Samples.BuiltInProcessors;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default");
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));

// Where the fake upstream lives: this app's own address (appsettings.json). Change it if you run the app on another port.
var upstream = new Uri(builder.Configuration["Upstream:BaseUrl"]!);

builder.Services
    .AddDynamicEndpoints(options =>
    {
        options.ReservedPrefixes.Add("/swagger");
        options.ReservedPrefixes.Add("/openapi");
        options.ReservedPrefixes.Add("/fake");   // the fake upstream stays static
        options.OpenApi.Title = "Built-in processors – dynamic endpoints";
    })
    .AddFromAssemblyContaining<Program>()
    // http-forward, webhook and response. AllowedHosts limits where admins can send requests (SSRF) – checked on save and on
    // every call. Here: only the fake upstream's host.
    .AddBuiltInProcessors(o => o.AllowedHosts.Add(upstream.Host))
    // sql-query (DynamicEndpoints.Sql) with any ADO.NET provider. Read-only checks are a safety net, not a sandbox – give it a
    // connection that can only read: here SQLite's read-only mode, in real life a database user with SELECT rights only.
    .AddSqlQueryProcessor(_ => new SqliteConnection(new SqliteConnectionStringBuilder(connectionString) { Mode = SqliteOpenMode.ReadOnly }.ToString()))
    .UseEntityFrameworkStore<AppDbContext>();

// The processors' HttpClient is the named client "DynamicEndpoints" – configure it like any other (timeouts, resilience, …).
builder.Services.AddHttpClient("DynamicEndpoints", client => client.DefaultRequestHeaders.UserAgent.ParseAdd("dynamic-endpoints-sample"));

builder.Services.AddSingleton<WebhookInbox>();
builder.Services.AddOpenApi();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();   // migrations in real life
    await db.SeedProductsAsync();
}

app.MapDynamicEndpoints();
app.MapDynamicEndpointsAdmin("/api/admin/endpoints");   // .RequireAuthorization("admin") in real life
app.MapDynamicEndpointsAdminUI("/admin", adminApiPath: "/api/admin/endpoints", options =>
{
    options.Title = "Built-in processors";
    options.SwaggerUrl = "/swagger";
    options.OpenApiUrl = "/openapi/dynamic.json";
});

// The "other services": a CRM for http-forward, webhook receivers (GET /fake/webhooks shows what arrived).
app.MapFakeUpstream();

app.MapDynamicEndpointsOpenApi("/openapi/dynamic.json");
app.MapOpenApi("/openapi/{documentName}.json");
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/openapi/dynamic.json", "Dynamic endpoints");
    options.SwaggerEndpoint("/openapi/v1.json", "Admin API and fake upstream");
});
app.MapGet("/", () => Results.Redirect("/admin/")).ExcludeFromDescription();

app.Run();

public partial class Program;
