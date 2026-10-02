// 11 – Observability: metrics and traces per dynamic endpoint with OpenTelemetry, printed by the console exporter – no
// collector, no Docker. With OTEL_EXPORTER_OTLP_ENDPOINT set they go to an OTLP endpoint too (the Aspire dashboard, Jaeger, …).
//
//   dotnet run --project samples/11-Observability      → http://localhost:5111/admin/ – watch the console
//   dotnet-counters monitor -n DynamicEndpoints.Samples.Observability --counters DynamicEndpoints      (no OpenTelemetry needed)

using DynamicEndpoints;
using DynamicEndpoints.Samples.Observability;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(builder.Configuration.GetConnectionString("Default")));

builder.Services
    .AddDynamicEndpoints(options =>
    {
        options.ReservedPrefixes.Add("/swagger");
        options.ReservedPrefixes.Add("/openapi");
        options.OpenApi.Title = "Observability – dynamic endpoints";
    })
    .AddFromAssemblyContaining<Program>()
    .AddResponseTemplateProcessor()
    .AddProcessor("work", async request =>
    {
        await Task.Delay(request.Get<int>("ms"), request.RequestAborted);
        return Results.Ok(new { sleptMs = request.Get<int>("ms") });
    }, "Sleeps for 'ms' milliseconds – shows up in processor.duration.")
    .AddProcessor("boom", IResult (_) => throw new InvalidOperationException("Something broke."), "Always throws.")
    .UseEntityFrameworkStore<AppDbContext>();

// The core emits through System.Diagnostics only: a Meter and an ActivitySource, both named "DynamicEndpoints".
// AddDynamicEndpointsInstrumentation() (DynamicEndpoints.OpenTelemetry) subscribes to them.
var console = builder.Configuration.GetValue("Telemetry:Console", true);
var interval = builder.Configuration.GetValue("Telemetry:MetricsIntervalSeconds", 15);
var otel = builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("dynamic-endpoints-observability-sample"))
    .WithMetrics(metrics =>
    {
        metrics
            .AddAspNetCoreInstrumentation()            // http.server.request.duration – with the dynamic route template
            .AddDynamicEndpointsInstrumentation();     // dynamic_endpoints.requests, .request.duration, .validation.failures, .processor.duration, .errors
        if (console)
        {
            metrics.AddConsoleExporter((_, reader) => reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = interval * 1000);
        }
    })
    .WithTracing(tracing =>
    {
        tracing
            .AddAspNetCoreInstrumentation(o => o.Filter = context => !context.Request.Path.StartsWithSegments("/admin"))
            .AddDynamicEndpointsInstrumentation();     // DynamicEndpoints.Request → Filters, Binding, Validation.{layer}, Processor
        if (console)
        {
            tracing.AddConsoleExporter();
        }
    });

// The same signals to an OTLP endpoint when one is configured – e.g. the Aspire dashboard or an OpenTelemetry collector.
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    otel.UseOtlpExporter();
}

builder.Services.AddOpenApi();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync(); // migrations in real life
}

// Unhandled exceptions of /boom: logged, counted (dynamic_endpoints.errors) and answered with a 500 problem – never a stack trace.
app.UseDynamicEndpointsErrorResponses(o => o.AppliesTo = context => !context.Request.Path.StartsWithSegments("/admin"));

app.MapDynamicEndpoints();
app.MapDynamicEndpointsAdmin("/api/admin/endpoints");   // .RequireAuthorization("admin") in real life
app.MapDynamicEndpointsAdminUI("/admin", adminApiPath: "/api/admin/endpoints", options =>
{
    options.Title = "Observability";
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
