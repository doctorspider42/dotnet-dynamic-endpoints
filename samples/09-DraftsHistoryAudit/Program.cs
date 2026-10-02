// 09 – Drafts, history & audit: save a change as a draft (validated, not routed), publish it now or at a set time, keep every
// revision for diffs and one-click rollbacks, and record who changed what in an audit log (EF Core table + ILogger).
//
//   dotnet run --project samples/09-DraftsHistoryAudit      → http://localhost:5109/admin/

using DynamicEndpoints;
using DynamicEndpoints.EntityFrameworkCore;
using DynamicEndpoints.Samples.DraftsHistoryAudit;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(builder.Configuration.GetConnectionString("Default")));

builder.Services
    .AddDynamicEndpoints(options =>
    {
        options.ReservedPrefixes.Add("/swagger");
        options.ReservedPrefixes.Add("/openapi");
        options.OpenApi.Title = "Drafts, history & audit – dynamic endpoints";
        // Due drafts are published by a background job – every 10 s by default, exactly once across instances. Faster for the demo.
        options.ScheduledPublishInterval = TimeSpan.FromSeconds(2);
    })
    .AddFromAssemblyContaining<Program>()
    .AddResponseTemplateProcessor()
    // The EF Core store keeps history and drafts (the in-memory store of DynamicEndpoints.Testing does too). The panel shows
    // "Save draft" and "History" because the admin API reports "revisions": true in GET /info.
    .UseEntityFrameworkStore<AppDbContext>()
    // Who changed which endpoint, when and how: to the log (category DynamicEndpoints.Audit) and to a table of AppDbContext,
    // which makes it queryable – GET /api/admin/endpoints/audit, and the panel's "Audit log".
    .AddAuditLog(audit => audit
        .ToLogger()
        .ToEntityFramework<AppDbContext>()
        // There is no authentication in this sample, so the user comes from a header. By default: the request's user name.
        .Configure(o => o.ResolveUser = http => http.Request.Headers["X-User"] is { Count: > 0 } user ? user.ToString() : http.User.Identity?.Name));

builder.Services.AddOpenApi();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync(); // migrations in real life
}

app.MapDynamicEndpoints();
// Drafts (/drafts, /{id}/draft, /{id}/publish), history (/{id}/revisions, /{id}/diff, /{id}/revisions/{revision}/rollback) and
// the audit log (/audit, /{id}/audit) are part of the admin API.
app.MapDynamicEndpointsAdmin("/api/admin/endpoints");   // .RequireAuthorization("admin") in real life
app.MapDynamicEndpointsAdminUI("/admin", adminApiPath: "/api/admin/endpoints", options =>
{
    options.Title = "Drafts, history & audit";
    options.SwaggerUrl = "/swagger";
    options.OpenApiUrl = "/openapi/dynamic.json";
});
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
