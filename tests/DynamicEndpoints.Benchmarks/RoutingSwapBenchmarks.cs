using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicEndpoints.Benchmarks;

/// <summary>
/// Cost of changing one endpoint while <see cref="EndpointCount"/> exist. Every change rebuilds the complete endpoint list
/// and fires the change token; routing has served a request before, so its matcher is rebuilt right in that callback.
/// Each iteration toggles the name of one definition, so every operation really writes.
/// </summary>
[MemoryDiagnoser]
public class RoutingSwapBenchmarks
{
    private BenchmarkHost _host = null!;
    private IDynamicEndpointStore _store = null!;
    private DynamicEndpointDefinition _target = null!;
    private string _lastRoute = null!;
    private bool _toggle;

    [Params(10, 100, 1000)]
    public int EndpointCount { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _host = await BenchmarkHost.StartAsync(b => b.AddProcessor("ok", r => Results.Ok(new { id = r.Get<int>("id") })));
        _store = _host.Services.GetRequiredService<IDynamicEndpointStore>();

        // One change set, so the routing table is published once instead of N times.
        var changes = _host.Manager.BeginChanges(_host.Services);
        for (var i = 0; i < EndpointCount; i++)
        {
            var created = await changes.CreateAsync(DynamicEndpoint.Get($"/swap/{i}/items/{{id}}").Named("A").HandledBy("ok").FromRoute("id", p => p.Integer()));
            _target ??= created;
        }

        await changes.ApplyAsync();
        _lastRoute = $"/swap/{EndpointCount - 1}/items/42";
        await Request();

        // Fail fast when an iteration wouldn't actually write.
        var before = _target.Revision;
        if ((await Upsert()).Revision != before + 1)
        {
            throw new InvalidOperationException("UpsertAsync did not write a new revision.");
        }
    }

    [GlobalCleanup]
    public async Task CleanupAsync() => await _host.DisposeAsync();

    /// <summary>Reference: a request without any change.</summary>
    [Benchmark(Description = "Request (no change)")]
    public async Task<int> Request() => await (await _host.Client.GetAsync(_lastRoute)).ReadAsync(200);

    /// <summary>Upsert of one endpoint: store write, compile, conflict check, routing table swap.</summary>
    [Benchmark(Description = "UpsertAsync (one endpoint)")]
    public async Task<DynamicEndpointDefinition> Upsert() =>
        await _host.Manager.UpsertAsync(_target with { Name = NextName() });

    /// <summary>Upsert followed by the first request routed by the new table.</summary>
    [Benchmark(Description = "UpsertAsync + first request")]
    public async Task<int> UpsertThenRequest()
    {
        await _host.Manager.UpsertAsync(_target with { Name = NextName() });
        return await (await _host.Client.GetAsync(_lastRoute)).ReadAsync(200);
    }

    /// <summary>
    /// Another instance changed one definition in the store: reload reads all definitions, recompiles the changed one
    /// and swaps the routing table.
    /// </summary>
    [Benchmark(Description = "ReloadAsync (one changed)")]
    public async Task Reload()
    {
        var current = (await _store.FindAsync(_target.Id, CancellationToken.None))!;
        await _store.UpdateAsync(
            current with { Name = NextName(), Revision = current.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow },
            current.Revision,
            CancellationToken.None);
        await _host.Manager.ReloadAsync();
    }

    private string NextName() => (_toggle = !_toggle) ? "B" : "A";
}
