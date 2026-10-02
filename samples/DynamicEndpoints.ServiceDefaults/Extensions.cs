using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// The usual .NET Aspire service defaults – OpenTelemetry, health checks, service discovery, resilience – plus the metrics and
/// traces of the dynamic endpoints (<c>AddDynamicEndpointsInstrumentation()</c> from <c>DynamicEndpoints.OpenTelemetry</c>).
/// Without an OTLP endpoint (outside Aspire) nothing is exported.
/// </summary>
public static class ServiceDefaultsExtensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();
        builder.AddDefaultHealthChecks();
        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler();
            http.AddServiceDiscovery();
        });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddDynamicEndpointsInstrumentation())    // dynamic_endpoints.* per endpoint
            .WithTracing(tracing => tracing
                .AddSource(builder.Environment.ApplicationName)
                .AddAspNetCoreInstrumentation(o =>
                    // Health probes would drown the interesting traces.
                    o.Filter = context => !context.Request.Path.StartsWithSegments(HealthEndpointPath) &&
                        !context.Request.Path.StartsWithSegments(AlivenessEndpointPath))
                .AddHttpClientInstrumentation()
                .AddDynamicEndpointsInstrumentation());   // binding, validation layers, processor

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);
        return builder;
    }

    /// <summary><c>/health</c> (ready) and <c>/alive</c> (live) – for orchestrators and container platforms.</summary>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapHealthChecks(HealthEndpointPath);
        app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions { Predicate = r => r.Tags.Contains("live") });
        return app;
    }
}
