// 10 – Caching & rate limits: Cache-Control, ETags with 304, output caching, rate limits and quotas – set per endpoint in the
// definition and enforced by ASP.NET Core's output caching and rate limiting. Rejections are 429s built by the error factory.
//
//   dotnet run --project samples/10-CachingAndRateLimits      → http://localhost:5110/admin/

using DynamicEndpoints;
using DynamicEndpoints.Samples.CachingAndRateLimits;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(builder.Configuration.GetConnectionString("Default")));

builder.Services
    .AddDynamicEndpoints(options =>
    {
        options.ReservedPrefixes.Add("/swagger");
        options.ReservedPrefixes.Add("/openapi");
        options.OpenApi.Title = "Caching & rate limits – dynamic endpoints";
    })
    .AddFromAssemblyContaining<Program>()
    // Answers with the time it ran and a counter – a cached response shows the old values.
    .AddProcessor("stamp", request => Results.Ok(new
    {
        endpoint = request.Endpoint.Name,
        parameters = request.Parameters,
        call = Interlocked.Increment(ref Stamp.Calls),
        generatedAt = DateTimeOffset.UtcNow,
    }), "Answers with the time it ran.")
    // The 429 of a rate limit or quota in the application's own format; everything else stays RFC 9457 problem details.
    .UseErrorResponses(error => error.Kind == DynamicErrorKind.TooManyRequests
        ? Results.Json(new { error = "rate_limited", message = error.Title, endpoint = error.Endpoint?.Name, requestId = error.RequestId },
            statusCode: error.StatusCode)
        : new DefaultDynamicErrorResponseFactory().CreateResponse(error))
    .UseEntityFrameworkStore<AppDbContext>();

// Rate limits and output caching of the definitions need the ASP.NET Core services (checked when a definition is saved)…
builder.Services.AddRateLimiter(_ => { });
builder.Services.AddOutputCache();

builder.Services.AddOpenApi();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync(); // migrations in real life
}

// …and the middleware. (Behind a proxy, UseForwardedHeaders() first, so the IpAddress partition sees the client.)
app.UseRateLimiter();
app.UseOutputCache();

app.MapDynamicEndpoints();
app.MapDynamicEndpointsAdmin("/api/admin/endpoints");   // .RequireAuthorization("admin") in real life
app.MapDynamicEndpointsAdminUI("/admin", adminApiPath: "/api/admin/endpoints", options =>
{
    options.Title = "Caching & rate limits";
    options.SwaggerUrl = "/swagger";
    options.OpenApiUrl = "/openapi/dynamic.json";
});
app.MapDynamicEndpointsOpenApi("/openapi/dynamic.json");   // documents Cache-Control/ETag, If-None-Match/304, the limit and the 429
app.MapOpenApi("/openapi/{documentName}.json");
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/openapi/dynamic.json", "Dynamic endpoints");
    options.SwaggerEndpoint("/openapi/v1.json", "Admin API");
});
app.MapGet("/", () => Results.Redirect("/admin/")).ExcludeFromDescription();

app.Run();

public partial class Program;

internal static class Stamp
{
    public static long Calls;
}
