extern alias Observability;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;

namespace DynamicEndpoints.Samples.Tests;

/// <summary>samples/11-Observability – the measurements and spans the sample exports, caught with plain System.Diagnostics listeners.</summary>
public sealed class ObservabilityTests : IAsyncLifetime
{
    private readonly SampleApp<Observability::Program> _app = new(("Telemetry:Console", "false"));
    private readonly ConcurrentBag<(string Instrument, string? Endpoint, string? Outcome, string? Layer)> _measurements = [];
    private readonly ConcurrentBag<(string Name, string? Endpoint, ActivityStatusCode Status)> _spans = [];
    private readonly MeterListener _meters = new();
    private readonly ActivityListener _activities;
    private HttpClient _client = null!;

    public ObservabilityTests()
    {
        _meters.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "DynamicEndpoints")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _meters.SetMeasurementEventCallback<long>((instrument, _, tags, _) => Record(instrument, tags));
        _meters.SetMeasurementEventCallback<double>((instrument, _, tags, _) => Record(instrument, tags));
        _meters.Start();

        _activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "DynamicEndpoints",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _spans.Add((activity.OperationName, activity.GetTagItem("dynamic_endpoint.name") as string, activity.Status)),
        };
        ActivitySource.AddActivityListener(_activities);
    }

    public Task InitializeAsync()
    {
        _client = _app.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _meters.Dispose();
        _activities.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Requests_validation_failures_and_errors_are_measured_per_endpoint()
    {
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/hello/Ada")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/orders", new { sku = "nope", quantity = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/orders", new { sku = "ANV-1", quantity = 50 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/work?ms=10")).StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, (await _client.GetAsync("/boom")).StatusCode);

        Assert.Contains(("dynamic_endpoints.requests", "Say hello", "processed", null), _measurements);
        Assert.Contains(("dynamic_endpoints.validation.failures", "Create order", null, "constraints"), _measurements);
        Assert.Contains(("dynamic_endpoints.validation.failures", "Create order", null, "rules"), _measurements);
        Assert.Contains(_measurements, m => m.Instrument == "dynamic_endpoints.processor.duration" && m.Endpoint == "Slow work");
        Assert.Contains(_measurements, m => m.Instrument == "dynamic_endpoints.errors" && m.Endpoint == "Always fails");

        Assert.Contains(("DynamicEndpoints.Processor", "Slow work", ActivityStatusCode.Unset), _spans);
        Assert.Contains(_spans, s => s.Name == "DynamicEndpoints.Request" && s.Endpoint == "Always fails" && s.Status == ActivityStatusCode.Error);
        Assert.Contains(_spans, s => s.Name.StartsWith("DynamicEndpoints.Validation.", StringComparison.Ordinal) && s.Endpoint == "Create order");
    }

    private void Record(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        string? endpoint = null, outcome = null, layer = null;
        foreach (var (key, value) in tags)
        {
            switch (key)
            {
                case "dynamic_endpoint.name": endpoint = value as string; break;
                case "dynamic_endpoint.outcome": outcome = value as string; break;
                case "dynamic_endpoint.validation.layer": layer = value as string; break;
            }
        }

        _measurements.Add((instrument.Name, endpoint, outcome, layer));
    }
}
