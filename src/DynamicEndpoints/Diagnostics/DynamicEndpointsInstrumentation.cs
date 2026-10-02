using System.Diagnostics;
using System.Diagnostics.Metrics;
using DynamicEndpoints.Runtime;
using static DynamicEndpoints.DynamicEndpointsTelemetry;

namespace DynamicEndpoints.Diagnostics;

/// <summary>
/// Metrics and activities of dynamic endpoint requests. Cheap when nobody listens: instruments are checked for
/// <see cref="Instrument.Enabled"/> and activities are only created for sampled requests.
/// </summary>
internal sealed class DynamicEndpointsInstrumentation
{
    private static readonly string? Version = typeof(DynamicEndpointsInstrumentation).Assembly.GetName().Version?.ToString();

    // The same boundaries ASP.NET Core uses for http.server.request.duration.
    private static readonly InstrumentAdvice<double> DurationAdvice = new()
    {
        HistogramBucketBoundaries = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10],
    };

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName, Version);

    private readonly Counter<long> _requests;
    private readonly Histogram<double> _requestDuration;
    private readonly Counter<long> _validationFailures;
    private readonly Histogram<double> _processorDuration;
    private readonly Counter<long> _errors;

    public DynamicEndpointsInstrumentation(IMeterFactory meterFactory)
    {
        // Owned (and disposed) by the factory – one meter per service provider, so parallel hosts don't mix their numbers.
        var meter = meterFactory.Create(new MeterOptions(MeterName) { Version = Version });
        _requests = meter.CreateCounter<long>(Instruments.Requests, "{request}", "Requests handled by dynamic endpoints.");
        _requestDuration = meter.CreateHistogram(Instruments.RequestDuration, "s",
            "Duration of handling a request to a dynamic endpoint: filters, binding, validation and processor.", advice: DurationAdvice);
        _validationFailures = meter.CreateCounter<long>(Instruments.ValidationFailures, "{request}",
            "Requests rejected by a validation layer of a dynamic endpoint.");
        _processorDuration = meter.CreateHistogram(Instruments.ProcessorDuration, "s",
            "Duration of the processor of a dynamic endpoint, including the execution of its result.", advice: DurationAdvice);
        _errors = meter.CreateCounter<long>(Instruments.Errors, "{exception}", "Unhandled exceptions in dynamic endpoints.");
    }

    public static Activity? StartActivity(string name, CompiledEndpoint endpoint)
    {
        var activity = ActivitySource.StartActivity(name, ActivityKind.Internal);
        if (activity is { IsAllDataRequested: true })
        {
            foreach (var tag in EndpointTags(endpoint))
            {
                activity.SetTag(tag.Key, tag.Value);
            }

            activity.SetTag(Tags.EndpointRevision, endpoint.Definition.Revision);
        }

        return activity;
    }

    public static Activity? StartValidation(string layer, CompiledEndpoint endpoint)
    {
        var activity = StartActivity(Activities.ValidationPrefix + layer, endpoint);
        activity?.SetTag(Tags.ValidationLayer, layer);
        return activity;
    }

    public static void StopValidation(Activity? activity, int errors)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag(Tags.ValidationErrors, errors);
        if (errors > 0)
        {
            activity.SetStatus(ActivityStatusCode.Error, "Validation failed.");
        }

        activity.Dispose();
    }

    public static void Failed(Activity? activity, Exception exception)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        activity.SetTag(Tags.ErrorType, exception.GetType().FullName);
        activity.AddException(exception);
    }

    public void RecordRequest(CompiledEndpoint endpoint, string outcome, int statusCode, TimeSpan duration)
    {
        if (!_requests.Enabled && !_requestDuration.Enabled)
        {
            return;
        }

        var tags = TagsOf(endpoint);
        tags.Add(Tags.Outcome, outcome);
        tags.Add(Tags.StatusCode, statusCode);
        _requests.Add(1, tags);
        _requestDuration.Record(duration.TotalSeconds, tags);
    }

    public void RecordValidationFailure(CompiledEndpoint endpoint, string layer)
    {
        if (_validationFailures.Enabled)
        {
            var tags = TagsOf(endpoint);
            tags.Add(Tags.ValidationLayer, layer);
            _validationFailures.Add(1, tags);
        }
    }

    public void RecordProcessor(CompiledEndpoint endpoint, TimeSpan duration)
    {
        if (_processorDuration.Enabled)
        {
            _processorDuration.Record(duration.TotalSeconds, TagsOf(endpoint));
        }
    }

    public void RecordError(CompiledEndpoint endpoint, Exception exception)
    {
        if (_errors.Enabled)
        {
            var tags = TagsOf(endpoint);
            tags.Add(Tags.ErrorType, exception.GetType().FullName);
            _errors.Add(1, tags);
        }
    }

    private static TagList TagsOf(CompiledEndpoint endpoint)
    {
        var tags = new TagList();
        foreach (var tag in EndpointTags(endpoint))
        {
            tags.Add(tag);
        }

        return tags;
    }

    // Built once per compiled endpoint – the definition never changes for it.
    private static KeyValuePair<string, object?>[] EndpointTags(CompiledEndpoint endpoint) =>
        endpoint.TelemetryTags ??=
        [
            new(Tags.EndpointId, endpoint.Definition.Id.ToString()),
            new(Tags.EndpointName, endpoint.Definition.Name ?? endpoint.Definition.Route),
            new(Tags.Route, endpoint.Definition.Route),
            new(Tags.Method, endpoint.Definition.Method),
            new(Tags.Processor, endpoint.ProcessorName),
        ];
}
