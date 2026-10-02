namespace DynamicEndpoints;

/// <summary>
/// Names of the metrics and traces the library emits through <c>System.Diagnostics</c> – no OpenTelemetry dependency needed.
/// With OpenTelemetry: <c>metrics.AddMeter(DynamicEndpointsTelemetry.MeterName)</c> and
/// <c>tracing.AddSource(DynamicEndpointsTelemetry.ActivitySourceName)</c>, or <c>AddDynamicEndpointsInstrumentation()</c>
/// from the <c>DynamicEndpoints.OpenTelemetry</c> package. <c>dotnet-counters monitor --counters DynamicEndpoints</c> works too.
/// </summary>
public static class DynamicEndpointsTelemetry
{
    /// <summary>Name of the <see cref="System.Diagnostics.Metrics.Meter"/>.</summary>
    public const string MeterName = "DynamicEndpoints";

    /// <summary>Name of the <see cref="System.Diagnostics.ActivitySource"/>.</summary>
    public const string ActivitySourceName = "DynamicEndpoints";

    /// <summary>Instruments of <see cref="MeterName"/>. Every measurement carries the <see cref="Tags"/> of its endpoint.</summary>
    public static class Instruments
    {
        /// <summary>Counter of handled requests, tagged with <see cref="Tags.Outcome"/> and <see cref="Tags.StatusCode"/>.</summary>
        public const string Requests = "dynamic_endpoints.requests";

        /// <summary>Histogram (seconds) of the whole handling: filters, binding, validation and processor.</summary>
        public const string RequestDuration = "dynamic_endpoints.request.duration";

        /// <summary>Counter of rejected requests per validation layer (<see cref="Tags.ValidationLayer"/>) that reported errors.</summary>
        public const string ValidationFailures = "dynamic_endpoints.validation.failures";

        /// <summary>Histogram (seconds) of the processor alone, including the execution of its result.</summary>
        public const string ProcessorDuration = "dynamic_endpoints.processor.duration";

        /// <summary>Counter of unhandled exceptions, tagged with <see cref="Tags.ErrorType"/>.</summary>
        public const string Errors = "dynamic_endpoints.errors";
    }

    /// <summary>Activities (spans) of <see cref="ActivitySourceName"/>, children of the ASP.NET Core request activity.</summary>
    public static class Activities
    {
        /// <summary>The whole handling of a request to a dynamic endpoint; parent of the spans below.</summary>
        public const string Request = "DynamicEndpoints.Request";

        /// <summary><see cref="IDynamicEndpointFilter.OnRequestAsync"/> of the registered filters.</summary>
        public const string Filters = "DynamicEndpoints.Filters";

        /// <summary>Reading and converting route, query, header, body and form parameters.</summary>
        public const string Binding = "DynamicEndpoints.Binding";

        /// <summary>One validation layer – the layer is in the <see cref="Tags.ValidationLayer"/> tag and the span name suffix.</summary>
        public const string ValidationPrefix = "DynamicEndpoints.Validation.";

        /// <summary>The processor, including the execution of its result.</summary>
        public const string Processor = "DynamicEndpoints.Processor";
    }

    /// <summary>Tags of the metrics and activities.</summary>
    public static class Tags
    {
        public const string EndpointId = "dynamic_endpoint.id";
        public const string EndpointName = "dynamic_endpoint.name";
        public const string EndpointRevision = "dynamic_endpoint.revision";
        public const string Processor = "dynamic_endpoint.processor";
        public const string Route = "http.route";
        public const string Method = "http.request.method";
        public const string StatusCode = "http.response.status_code";

        /// <summary>One of <see cref="Outcomes"/>.</summary>
        public const string Outcome = "dynamic_endpoint.outcome";

        /// <summary>One of <see cref="ValidationLayers"/>.</summary>
        public const string ValidationLayer = "dynamic_endpoint.validation.layer";

        /// <summary>Number of validation errors (activities only).</summary>
        public const string ValidationErrors = "dynamic_endpoint.validation.errors";

        /// <summary>Full name of the exception type.</summary>
        public const string ErrorType = "error.type";
    }

    /// <summary>Values of <see cref="Tags.Outcome"/>.</summary>
    public static class Outcomes
    {
        /// <summary>The request reached the processor.</summary>
        public const string Processed = "processed";

        /// <summary>Rejected by binding or validation (400, 413, 415).</summary>
        public const string Rejected = "rejected";

        /// <summary>A filter answered the request before it was read (e.g. a 403).</summary>
        public const string ShortCircuited = "short_circuited";

        /// <summary>An exception escaped the processor, a validator or a filter.</summary>
        public const string Error = "error";
    }

    /// <summary>Values of <see cref="Tags.ValidationLayer"/>, in the order the layers run.</summary>
    public static class ValidationLayers
    {
        /// <summary>Reading the request: malformed or too large bodies, wrong media types, missing or unconvertible values.</summary>
        public const string Binding = "binding";

        /// <summary>JSON Schema constraints, patterns, formats and file limits.</summary>
        public const string Constraints = "constraints";

        /// <summary>Custom validators attached to parameters.</summary>
        public const string ParameterValidators = "parameter_validators";

        /// <summary>JsonLogic business rules.</summary>
        public const string Rules = "rules";

        /// <summary>Custom validators of the whole request.</summary>
        public const string RequestValidators = "request_validators";
    }
}
