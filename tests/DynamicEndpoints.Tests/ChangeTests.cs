using System.Collections.Concurrent;
using System.Net;
using DynamicEndpoints.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicEndpoints.Tests;

public sealed class ChangeTests
{
    [Fact]
    public async Task Upsert_creates_replaces_without_a_revision_and_skips_unchanged_content()
    {
        await using var host = await TestHost.StartAsync();
        var id = Guid.NewGuid();

        var created = await host.Manager.UpsertAsync(DynamicEndpoint.Get("/upsert").WithId(id).HandledBy("echo"));
        Assert.Equal(1, created.Revision);

        var same = await host.Manager.UpsertAsync(DynamicEndpoint.Get("/upsert").WithId(id).HandledBy("echo"));
        Assert.Equal(1, same.Revision);

        // No revision sent – last writer wins.
        var changed = await host.Manager.UpsertAsync(DynamicEndpoint.Get("/upsert").WithId(id).Named("renamed").HandledBy("echo"));
        Assert.Equal(2, changed.Revision);
        Assert.Equal("renamed", (await host.Manager.GetAsync(id))!.Definition.Name);
        Assert.Equal(created.CreatedAt, changed.CreatedAt);
    }

    [Fact]
    public async Task Change_handlers_see_local_changes_with_the_previous_definition()
    {
        var changes = new ConcurrentQueue<DynamicEndpointChangedEvent>();
        await using var host = await TestHost.StartAsync(configure: b => b.OnChanged((change, _) =>
        {
            changes.Enqueue(change);
            return Task.CompletedTask;
        }));

        var created = await host.Manager.CreateAsync(DynamicEndpoint.Get("/audited").HandledBy("echo"));
        await host.Manager.SetEnabledAsync(created.Id, false);
        await host.Manager.DeleteAsync(created.Id);

        Assert.Collection(changes,
            c => Assert.Equal((DynamicEndpointChangeKind.Created, DynamicEndpointChangeOrigin.Local, (int?)1, (int?)null),
                (c.Kind, c.Origin, c.Definition?.Revision, c.Previous?.Revision)),
            c => Assert.Equal((DynamicEndpointChangeKind.Updated, false, true),
                (c.Kind, c.Definition!.Enabled, c.Previous!.Enabled)),
            c => Assert.Equal((DynamicEndpointChangeKind.Deleted, (DynamicEndpointDefinition?)null, 2),
                (c.Kind, c.Definition, c.Previous!.Revision)));
    }

    [Fact]
    public async Task A_failing_change_handler_does_not_undo_the_change()
    {
        await using var host = await TestHost.StartAsync(configure: b => b.OnChanged((_, _) => throw new InvalidOperationException("audit down")));

        await host.Manager.CreateAsync(DynamicEndpoint.Get("/still-there").HandledBy("echo"));

        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/still-there")).StatusCode);
    }

    [Fact]
    public async Task Change_set_writes_through_the_given_store_and_routes_only_after_apply()
    {
        await using var host = await TestHost.StartAsync();
        var store = new InMemoryDynamicEndpointStore();

        var changes = host.Manager.BeginChanges(store);
        var a = await changes.CreateAsync(DynamicEndpoint.Get("/staged/a").HandledBy("echo"));
        await changes.UpsertAsync(DynamicEndpoint.Get("/staged/b").HandledBy("echo"));
        await changes.UpdateAsync(DynamicEndpoint.Get("/staged/a").WithId(a.Id).Named("a").HandledBy("echo").Build() with { Revision = 1 });

        Assert.Equal(2, (await store.GetAllAsync(default)).Count);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/staged/a")).StatusCode);

        // Created and changed in one unit of work is one creation for everybody else.
        Assert.Equal([DynamicEndpointChangeKind.Created, DynamicEndpointChangeKind.Created], changes.Changes.Select(c => c.Kind));

        await changes.ApplyAsync();

        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/staged/a")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/staged/b")).StatusCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => changes.ApplyAsync());
    }

    [Fact]
    public async Task Change_set_detects_route_conflicts_between_its_own_changes()
    {
        await using var host = await TestHost.StartAsync();
        var changes = host.Manager.BeginChanges(new InMemoryDynamicEndpointStore());
        await changes.CreateAsync(DynamicEndpoint.Get("/twice").HandledBy("echo"));

        var ex = await Assert.ThrowsAsync<DynamicEndpointValidationException>(() =>
            changes.CreateAsync(DynamicEndpoint.Get("/twice").HandledBy("echo")));
        Assert.Contains("route", ex.Errors.Keys);
    }

    [Fact]
    public async Task Change_notifications_update_other_instances_immediately()
    {
        var store = new InMemoryDynamicEndpointStore();
        var notifier = new InMemoryDynamicEndpointChangeNotifier();
        var remote = new ConcurrentQueue<DynamicEndpointChangedEvent>();

        DynamicEndpointsTestServerOptions Instance(Action<IDynamicEndpointsBuilder>? configure = null) => new()
        {
            Store = store,
            DynamicEndpoints = b =>
            {
                b.AddProcessor("echo", r => Microsoft.AspNetCore.Http.Results.Ok(r.Parameters)).UseChangeNotifier(notifier);
                configure?.Invoke(b);
            },
        };

        await using var a = await DynamicEndpointsTestServer.StartAsync(Instance());
        await using var b = await DynamicEndpointsTestServer.StartAsync(Instance(builder => builder.OnChanged((change, _) =>
        {
            remote.Enqueue(change);
            return Task.CompletedTask;
        })));

        var created = await a.Manager.CreateAsync(DynamicEndpoint.Get("/everywhere").HandledBy("echo"));
        await Eventually(async () => (await b.Client.GetAsync("/everywhere")).StatusCode == HttpStatusCode.OK);

        await a.Manager.SetEnabledAsync(created.Id, false);
        await Eventually(async () => (await b.Client.GetAsync("/everywhere")).StatusCode == HttpStatusCode.NotFound);

        Assert.All(remote, c => Assert.Equal(DynamicEndpointChangeOrigin.Remote, c.Origin));
        Assert.Equal([DynamicEndpointChangeKind.Created, DynamicEndpointChangeKind.Updated], remote.Select(c => c.Kind));
        Assert.Equal([created.Id], notifier.Published.First().EndpointIds);
    }

    [Fact]
    public void Notifications_round_trip_as_json_and_reject_garbage()
    {
        var notification = new DynamicEndpointChangeNotification("a", [Guid.NewGuid()]);

        Assert.Equal(notification.EndpointIds, DynamicEndpointChangeNotification.Parse(notification.ToJson())!.EndpointIds);
        Assert.Null(DynamicEndpointChangeNotification.Parse("not json"));
        Assert.Null(DynamicEndpointChangeNotification.Parse("{}"));

        // Large batches stay below transport limits (PostgreSQL NOTIFY: 8000 bytes) – receivers reload everything anyway.
        var large = new DynamicEndpointChangeNotification("a", Enumerable.Range(0, 500).Select(_ => Guid.NewGuid()).ToList());
        Assert.True(large.ToJson().Length < 8000);
    }

    internal static async Task Eventually(Func<Task<bool>> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The condition was not met in time.");
            }

            await Task.Delay(25);
        }
    }
}
