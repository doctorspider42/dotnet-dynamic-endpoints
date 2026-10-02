using DynamicEndpoints;
using OpenTelemetry.Trace;

namespace OpenTelemetry.Metrics;

public static class DynamicEndpointsMeterProviderBuilderExtensions
{
    /// <summary>
    /// Collects the metrics of dynamic endpoints (<see cref="DynamicEndpointsTelemetry.MeterName"/>): requests, request and
    /// processor duration, validation failures by layer and errors – each tagged with the endpoint's id, name, route and method.
    /// </summary>
    public static MeterProviderBuilder AddDynamicEndpointsInstrumentation(this MeterProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddMeter(DynamicEndpointsTelemetry.MeterName);
    }
}

public static class DynamicEndpointsTracerProviderBuilderExtensions
{
    /// <summary>
    /// Collects the spans of dynamic endpoint requests (<see cref="DynamicEndpointsTelemetry.ActivitySourceName"/>): filters,
    /// binding, every validation layer and the processor, as children of the ASP.NET Core request span.
    /// </summary>
    public static TracerProviderBuilder AddDynamicEndpointsInstrumentation(this TracerProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddSource(DynamicEndpointsTelemetry.ActivitySourceName);
    }
}
