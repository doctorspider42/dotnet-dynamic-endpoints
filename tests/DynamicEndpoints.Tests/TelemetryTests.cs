using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using static DynamicEndpoints.DynamicEndpointsTelemetry;

namespace DynamicEndpoints.Tests;

public sealed class TelemetryTests
{
    private static Task<TestHost> StartAsync() => TestHost.StartAsync(
        configure: b => b
            .AddProcessor("boom", IResult (_) => throw new InvalidOperationException("boom"))
            .AddValidator("never", c =>
            {
                c.AddError("Never valid.");
                return ValueTask.CompletedTask;
            }, DynamicValidatorTargets.Request),
        configureApp: app => app.UseDynamicEndpointsErrorResponses());

    [Fact]
    public async Task Requests_validation_failures_processor_and_errors_are_measured_per_endpoint()
    {
        await using var host = await StartAsync();
        using var metrics = new MetricRecorder(host.Services.GetRequiredService<IMeterFactory>());
        var orders = await host.Manager.CreateAsync(DynamicEndpoint.Post("/metrics/orders").Named("Orders").HandledBy("echo")
            .FromBody("quantity", p => p.Integer().Required().Range(1, 100))
            .WithRule("""{ "!=": [{ "var": "quantity" }, 13] }""", "Unlucky.", "quantity"));
        var guarded = await host.Manager.CreateAsync(DynamicEndpoint.Get("/metrics/guarded").HandledBy("echo").ValidatedBy("never"));
        var boom = await host.Manager.CreateAsync(DynamicEndpoint.Get("/metrics/boom").HandledBy("boom"));

        Assert.Equal(HttpStatusCode.OK, (await Post("/metrics/orders", """{ "quantity": 5 }""")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("/metrics/orders", """{ "quantity": 500 }""")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("/metrics/orders", """{ "quantity": 13 }""")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("/metrics/orders", """{ "quantity": """)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync("/metrics/guarded")).StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, (await host.Client.GetAsync("/metrics/boom")).StatusCode);

        var requests = metrics.For(Instruments.Requests, orders.Id);
        Assert.Equal(4, requests.Count);
        Assert.All(requests, m =>
        {
            Assert.Equal("Orders", m.Tags[Tags.EndpointName]);
            Assert.Equal("/metrics/orders", m.Tags[Tags.Route]);
            Assert.Equal("POST", m.Tags[Tags.Method]);
            Assert.Equal("echo", m.Tags[Tags.Processor]);
        });
        Assert.Single(requests, m => (string?)m.Tags[Tags.Outcome] == Outcomes.Processed && (int?)m.Tags[Tags.StatusCode] == 200);
        Assert.Equal(3, requests.Count(m => (string?)m.Tags[Tags.Outcome] == Outcomes.Rejected && (int?)m.Tags[Tags.StatusCode] == 400));
        Assert.Equal(4, metrics.For(Instruments.RequestDuration, orders.Id).Count);
        Assert.Single(metrics.For(Instruments.ProcessorDuration, orders.Id));

        string[] Layers(Guid id) => metrics.For(Instruments.ValidationFailures, id).Select(m => (string)m.Tags[Tags.ValidationLayer]!).Order().ToArray();
        Assert.Equal([ValidationLayers.Binding, ValidationLayers.Constraints, ValidationLayers.Rules], Layers(orders.Id));
        Assert.Equal([ValidationLayers.RequestValidators], Layers(guarded.Id));

        var error = Assert.Single(metrics.For(Instruments.Errors, boom.Id));
        Assert.Equal(typeof(InvalidOperationException).FullName, error.Tags[Tags.ErrorType]);
        var failed = Assert.Single(metrics.For(Instruments.Requests, boom.Id));
        Assert.Equal(Outcomes.Error, failed.Tags[Tags.Outcome]);
        Assert.Equal(500, failed.Tags[Tags.StatusCode]);

        Task<HttpResponseMessage> Post(string path, string json) =>
            host.Client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));
    }

    [Fact]
    public async Task Binding_validation_layers_and_the_processor_are_traced()
    {
        await using var host = await StartAsync();
        var endpoint = await host.Manager.CreateAsync(DynamicEndpoint.Post("/traced/orders").HandledBy("echo")
            .FromBody("quantity", p => p.Integer().Required())
            .WithRule("""{ ">": [{ "var": "quantity" }, 0] }""", "Positive.", "quantity"));
        var id = endpoint.Id.ToString();

        var activities = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                if (a.GetTagItem(Tags.EndpointId) as string == id)
                {
                    activities.Enqueue(a);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        await host.Client.PostAsync("/traced/orders", new StringContent("""{ "quantity": 0 }""", Encoding.UTF8, "application/json"));

        var request = Assert.Single(activities, a => a.OperationName == Activities.Request);
        Assert.Equal(Outcomes.Rejected, request.GetTagItem(Tags.Outcome));
        Assert.Equal(1, request.GetTagItem(Tags.EndpointRevision));
        var children = activities.Where(a => a.ParentSpanId == request.SpanId).Select(a => a.OperationName).ToList();
        Assert.Equal([Activities.Binding, Activities.ValidationPrefix + ValidationLayers.Constraints, Activities.ValidationPrefix + ValidationLayers.Rules], children);
        var rules = activities.Single(a => a.OperationName == Activities.ValidationPrefix + ValidationLayers.Rules);
        Assert.Equal(ActivityStatusCode.Error, rules.Status);
        Assert.Equal(1, rules.GetTagItem(Tags.ValidationErrors));

        activities.Clear();
        await host.Client.PostAsync("/traced/orders", new StringContent("""{ "quantity": 3 }""", Encoding.UTF8, "application/json"));
        Assert.Contains(activities, a => a.OperationName == Activities.Processor && 200.Equals(a.GetTagItem(Tags.StatusCode)));
        Assert.Equal(Outcomes.Processed, activities.Single(a => a.OperationName == Activities.Request).GetTagItem(Tags.Outcome));
    }

    [Fact]
    public async Task OpenTelemetry_extensions_register_the_meter_and_the_source()
    {
        var metrics = new List<OpenTelemetry.Metrics.Metric>();
        var spans = new List<Activity>();
        using var meterProvider = OpenTelemetry.Sdk.CreateMeterProviderBuilder()
            .AddDynamicEndpointsInstrumentation()
            .AddInMemoryExporter(metrics)
            .Build();
        using var tracerProvider = OpenTelemetry.Sdk.CreateTracerProviderBuilder()
            .AddDynamicEndpointsInstrumentation()
            .AddInMemoryExporter(spans)
            .Build();

        await using var host = await StartAsync();
        var endpoint = await host.Manager.CreateAsync(DynamicEndpoint.Get("/otel/ping").HandledBy("echo"));
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/otel/ping")).StatusCode);
        meterProvider.ForceFlush();
        tracerProvider.ForceFlush();

        var id = endpoint.Id.ToString();
        var requests = metrics.Where(m => m.Name == Instruments.Requests).SelectMany(m =>
        {
            var points = new List<OpenTelemetry.Metrics.MetricPoint>();
            foreach (ref readonly var point in m.GetMetricPoints())
            {
                points.Add(point);
            }

            return points;
        }).Where(p =>
        {
            foreach (var tag in p.Tags)
            {
                if (tag.Key == Tags.EndpointId && (string?)tag.Value == id)
                {
                    return true;
                }
            }

            return false;
        });
        Assert.Equal(1, Assert.Single(requests).GetSumLong());
        Assert.Contains(spans, s => s.OperationName == Activities.Processor && (string?)s.GetTagItem(Tags.EndpointId) == id);
    }

    /// <summary>Collects the measurements of one host's meter (other tests run in parallel with their own meters).</summary>
    private sealed class MetricRecorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<(string Instrument, Dictionary<string, object?> Tags)> _measurements = new();

        public MetricRecorder(IMeterFactory factory)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == MeterName && instrument.Meter.Scope == factory)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((i, _, tags, _) => Record(i, tags));
            _listener.SetMeasurementEventCallback<double>((i, _, tags, _) => Record(i, tags));
            _listener.Start();
        }

        public List<(string Instrument, Dictionary<string, object?> Tags)> For(string instrument, Guid endpointId) =>
            _measurements.Where(m => m.Instrument == instrument && (string?)m.Tags[Tags.EndpointId] == endpointId.ToString()).ToList();

        public void Dispose() => _listener.Dispose();

        private void Record(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags) =>
            _measurements.Enqueue((instrument.Name, new Dictionary<string, object?>(tags.ToArray())));
    }
}
