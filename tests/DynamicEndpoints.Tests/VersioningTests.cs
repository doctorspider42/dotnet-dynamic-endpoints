using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DynamicEndpoints.EntityFrameworkCore;
using DynamicEndpoints.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicEndpoints.Tests;

public sealed class VersioningTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"dynamic-endpoints-versioning-{Guid.NewGuid():N}.db");

    private string ConnectionString => $"Data Source={_databasePath};Pooling=False";

    [Fact]
    public async Task Every_change_is_a_revision_that_can_be_compared_and_rolled_back()
    {
        await using var host = await TestHost.StartAsync();
        var created = await host.Manager.CreateAsync(DynamicEndpoint.Get("/history/v1").Named("History").HandledBy("echo")
            .FromQuery("q", p => p.MaxLength(5)));
        var updated = await host.Manager.UpdateAsync(created with
        {
            Route = "/history/v2",
            Parameters = [created.Parameters[0] with { MaxLength = 10 }],
        });
        await host.Manager.SetEnabledAsync(created.Id, false);

        var history = await host.Manager.GetHistoryAsync(created.Id);
        Assert.Equal([3, 2, 1], history.Select(r => r.Revision));
        Assert.Equal([DynamicEndpointRevisionKind.Disabled, DynamicEndpointRevisionKind.Updated, DynamicEndpointRevisionKind.Created], history.Select(r => r.Kind));
        Assert.Equal("/history/v1", history[2].Definition.Route);

        var diff = await host.Manager.DiffAsync(created.Id, 1, 3);
        Assert.Equal(["enabled", "parameters[q].maxLength", "route"], diff.Select(d => d.Path).Order(StringComparer.Ordinal));
        var route = diff.Single(d => d.Path == "route");
        Assert.Equal(DynamicEndpointDifferenceKind.Changed, route.Kind);
        Assert.Equal("/history/v1", route.From!.GetValue<string>());
        Assert.Equal("/history/v2", route.To!.GetValue<string>());

        var rolledBack = await host.Manager.RollbackAsync(created.Id, 1);
        Assert.Equal(4, rolledBack.Revision);
        Assert.Equal("/history/v1", rolledBack.Route);
        Assert.True(rolledBack.Enabled);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/history/v1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/history/v2")).StatusCode);

        var latest = (await host.Manager.GetHistoryAsync(created.Id))[0];
        Assert.Equal(DynamicEndpointRevisionKind.RolledBack, latest.Kind);
        Assert.Equal(1, latest.SourceRevision);
        Assert.Empty(await host.Manager.DiffAsync(created.Id, 1, 4));
        Assert.Equal(updated.Route, (await host.Manager.GetRevisionAsync(created.Id, 2))!.Definition.Route);
        await Assert.ThrowsAsync<DynamicEndpointNotFoundException>(() => host.Manager.RollbackAsync(created.Id, 42));
    }

    [Fact]
    public async Task Drafts_are_not_routed_until_they_are_published()
    {
        await using var host = await TestHost.StartAsync();

        // A new endpoint, drafted.
        var draft = await host.Manager.SaveDraftAsync(DynamicEndpoint.Get("/drafts/new").HandledBy("echo"), comment: "first try");
        Assert.NotEqual(Guid.Empty, draft.EndpointId);
        Assert.Equal(0, draft.BaseRevision);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/drafts/new")).StatusCode);
        var state = Assert.Single(await host.Manager.ListAsync());
        Assert.Equal(DynamicEndpointStatus.Draft, state.Status);
        Assert.Equal(DynamicEndpointStatus.Draft, (await host.Manager.GetAsync(draft.EndpointId))!.Status);
        Assert.All(await host.Manager.DiffDraftAsync(draft.EndpointId), d => Assert.Equal(DynamicEndpointDifferenceKind.Added, d.Kind));

        var published = await host.Manager.PublishAsync(draft.EndpointId);
        Assert.Equal(1, published.Revision);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/drafts/new")).StatusCode);
        Assert.Null(await host.Manager.GetDraftAsync(draft.EndpointId));
        var first = Assert.Single(await host.Manager.GetHistoryAsync(draft.EndpointId));
        Assert.Equal(DynamicEndpointRevisionKind.Published, first.Kind);
        Assert.Equal("first try", first.Comment);

        // A change of the published endpoint, drafted: the published revision keeps serving.
        await host.Manager.SaveDraftAsync(published with { Route = "/drafts/changed" });
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/drafts/new")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/drafts/changed")).StatusCode);
        var listed = Assert.Single(await host.Manager.ListAsync());
        Assert.Equal(DynamicEndpointStatus.Active, listed.Status);
        Assert.Equal("/drafts/changed", listed.Draft!.Definition.Route);
        Assert.Equal("route", Assert.Single(await host.Manager.DiffDraftAsync(published.Id)).Path);

        // Somebody changes the published endpoint in the meantime – the draft is outdated.
        var meanwhile = await host.Manager.UpdateAsync(published with { Name = "renamed" });
        await Assert.ThrowsAsync<DynamicEndpointConcurrencyException>(() => host.Manager.PublishAsync(published.Id));
        await host.Manager.SaveDraftAsync(meanwhile with { Route = "/drafts/changed" });
        var republished = await host.Manager.PublishAsync(published.Id);

        Assert.Equal(3, republished.Revision);
        Assert.Equal("renamed", republished.Name);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/drafts/changed")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/drafts/new")).StatusCode);

        // Discarding leaves the published revision alone, deleting a draft-only endpoint removes it.
        await host.Manager.SaveDraftAsync(republished with { Name = "discard me" });
        Assert.True(await host.Manager.DiscardDraftAsync(published.Id));
        Assert.Equal("renamed", (await host.Manager.GetAsync(published.Id))!.Definition.Name);
        var orphan = await host.Manager.SaveDraftAsync(DynamicEndpoint.Get("/drafts/orphan").HandledBy("echo"));
        Assert.True(await host.Manager.DeleteAsync(orphan.EndpointId));
        Assert.Null(await host.Manager.GetAsync(orphan.EndpointId));
    }

    [Fact]
    public async Task Drafts_are_validated_when_saved()
    {
        await using var host = await TestHost.StartAsync();
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/taken").HandledBy("echo"));

        var invalid = await Assert.ThrowsAsync<DynamicEndpointValidationException>(() =>
            host.Manager.SaveDraftAsync(DynamicEndpoint.Get("/taken").HandledBy("echo")));
        Assert.Contains("route", invalid.Errors.Keys);
        await Assert.ThrowsAsync<DynamicEndpointValidationException>(() =>
            host.Manager.SaveDraftAsync(DynamicEndpoint.Get("/drafts/unknown").HandledBy("no-such-processor")));
        await Assert.ThrowsAsync<DynamicEndpointNotFoundException>(() =>
            host.Manager.SaveDraftAsync(DynamicEndpoint.Get("/drafts/gone").HandledBy("echo").Build() with { Id = Guid.NewGuid(), Revision = 3 }));
        Assert.Empty(await host.Manager.ListDraftsAsync());
    }

    [Fact]
    public async Task Scheduled_drafts_are_published_when_due()
    {
        await using var host = await TestHost.StartAsync(options: o => o.ScheduledPublishInterval = null);
        var due = await host.Manager.SaveDraftAsync(DynamicEndpoint.Get("/scheduled/due").HandledBy("echo"), DateTimeOffset.UtcNow.AddMinutes(-1));
        var later = await host.Manager.SaveDraftAsync(DynamicEndpoint.Get("/scheduled/later").HandledBy("echo"), DateTimeOffset.UtcNow.AddHours(1));
        var broken = await host.Manager.SaveDraftAsync(DynamicEndpoint.Get("/scheduled/broken").HandledBy("echo"), DateTimeOffset.UtcNow.AddMinutes(-1));
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/scheduled/broken").HandledBy("echo")); // takes the route of the draft

        var published = await host.Manager.PublishDueAsync();

        Assert.Equal(due.EndpointId, Assert.Single(published).Id);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/scheduled/due")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/scheduled/later")).StatusCode);
        Assert.NotNull((await host.Manager.GetDraftAsync(later.EndpointId))!.PublishAt);
        Assert.Null((await host.Manager.GetDraftAsync(broken.EndpointId))!.PublishAt); // kept, but no longer retried
        Assert.Empty(await host.Manager.PublishDueAsync());
    }

    [Fact]
    public async Task The_background_scheduler_publishes_due_drafts()
    {
        await using var host = await TestHost.StartAsync(options: o => o.ScheduledPublishInterval = TimeSpan.FromMilliseconds(50));
        await host.Manager.SaveDraftAsync(DynamicEndpoint.Get("/scheduled/background").HandledBy("echo"), DateTimeOffset.UtcNow);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((await host.Client.GetAsync("/scheduled/background")).StatusCode != HttpStatusCode.OK)
        {
            Assert.True(DateTime.UtcNow < deadline, "The scheduled draft was not published.");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task The_admin_api_exposes_drafts_history_diffs_and_rollback()
    {
        await using var host = await TestHost.StartAsync();
        const string api = "/admin/endpoints";

        var draftResponse = await host.Client.PostAsJsonAsync($"{api}/drafts", new
        {
            definition = new { method = "GET", route = "/api-drafts/v1", processor = "echo" },
            comment = "via REST",
        });
        Assert.Equal(HttpStatusCode.Created, draftResponse.StatusCode);
        var id = (await draftResponse.Content.ReadFromJsonAsync<JsonObject>())!["endpointId"]!.GetValue<Guid>();
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync($"{api}/{id}/draft")).StatusCode);
        Assert.Single((await host.Client.GetFromJsonAsync<JsonArray>($"{api}/drafts"))!);

        Assert.Equal(HttpStatusCode.OK, (await host.Client.PostAsync($"{api}/{id}/publish", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/api-drafts/v1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.PostAsync($"{api}/{id}/publish", null)).StatusCode);

        var put = await host.Client.PutAsJsonAsync($"{api}/{id}/draft", new
        {
            definition = new { method = "GET", route = "/api-drafts/v2", processor = "echo", revision = 1 },
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var draftDiff = await host.Client.GetFromJsonAsync<JsonArray>($"{api}/{id}/draft/diff");
        Assert.Equal("route", draftDiff![0]!["path"]!.GetValue<string>());
        await host.Client.PostAsync($"{api}/{id}/publish", null);

        var revisions = await host.Client.GetFromJsonAsync<JsonArray>($"{api}/{id}/revisions");
        Assert.Equal([2, 1], revisions!.Select(r => r!["revision"]!.GetValue<int>()));
        Assert.Equal("Published", revisions![1]!["kind"]!.GetValue<string>());
        Assert.Equal("via REST", revisions[1]!["comment"]!.GetValue<string>());
        Assert.Equal("/api-drafts/v1", (await host.Client.GetFromJsonAsync<JsonObject>($"{api}/{id}/revisions/1"))!["definition"]!["route"]!.GetValue<string>());

        var diff = await host.Client.GetFromJsonAsync<JsonArray>($"{api}/{id}/diff?from=1");
        Assert.Equal("/api-drafts/v2", Assert.Single(diff!)!["to"]!.GetValue<string>());

        var rollback = await host.Client.PostAsync($"{api}/{id}/revisions/1/rollback", null);
        Assert.Equal(HttpStatusCode.OK, rollback.StatusCode);
        Assert.Equal(3, (await rollback.Content.ReadFromJsonAsync<JsonObject>())!["revision"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/api-drafts/v1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.PostAsync($"{api}/{id}/revisions/9/rollback", null)).StatusCode);

        var conflict = await host.Client.PutAsJsonAsync($"{api}/{id}/draft", new
        {
            definition = new { method = "GET", route = "/api-drafts/v3", processor = "echo", revision = 7 },
        });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.DeleteAsync($"{api}/{id}")).StatusCode);
        Assert.Empty((await host.Client.GetFromJsonAsync<JsonArray>($"{api}/{id}/revisions"))!);
    }

    [Fact]
    public async Task Stores_without_history_keep_working_and_answer_501()
    {
        await using var host = await TestHost.StartAsync(configure: b => b.UseStore<PlainStore>());
        var created = await host.Manager.CreateAsync(DynamicEndpoint.Get("/plain").HandledBy("echo"));
        await host.Manager.UpdateAsync(created with { Name = "still works" });

        await Assert.ThrowsAsync<NotSupportedException>(() => host.Manager.SaveDraftAsync(DynamicEndpoint.Get("/plain/draft").HandledBy("echo")));
        await Assert.ThrowsAsync<NotSupportedException>(() => host.Manager.GetHistoryAsync(created.Id));
        Assert.Empty(await host.Manager.PublishDueAsync());
        Assert.Equal(HttpStatusCode.NotImplemented, (await host.Client.GetAsync("/admin/endpoints/drafts")).StatusCode);
        Assert.Equal(HttpStatusCode.NotImplemented, (await host.Client.GetAsync($"/admin/endpoints/{created.Id}/revisions")).StatusCode);
    }

    [Fact]
    public async Task EF_Core_keeps_history_and_drafts_across_restarts()
    {
        Guid id;
        await using (var host = await StartEfAsync())
        {
            var created = await host.Manager.CreateAsync(DynamicEndpoint.Get("/ef-history/v1").HandledBy("echo"));
            id = created.Id;
            await host.Manager.UpdateAsync(created with { Route = "/ef-history/v2" });
            await host.Manager.SaveDraftAsync(created with { Revision = 2, Route = "/ef-history/v3" }, DateTimeOffset.UtcNow.AddDays(1), "tomorrow");
            var orphan = await host.Manager.SaveDraftAsync(DynamicEndpoint.Get("/ef-history/orphan").HandledBy("echo"));
            Assert.True(await host.Manager.DeleteAsync(orphan.EndpointId));
        }

        await using (var host = await StartEfAsync())
        {
            Assert.Equal([2, 1], (await host.Manager.GetHistoryAsync(id)).Select(r => r.Revision));
            var draft = Assert.Single(await host.Manager.ListDraftsAsync());
            Assert.Equal("tomorrow", draft.Comment);
            Assert.Equal(2, draft.BaseRevision);
            Assert.NotNull(draft.PublishAt);

            await host.Manager.PublishAsync(id);
            Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/ef-history/v3")).StatusCode);
            Assert.Empty(await host.Manager.ListDraftsAsync());
            await host.Manager.RollbackAsync(id, 1);
            Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/ef-history/v1")).StatusCode);
            Assert.Equal([DynamicEndpointRevisionKind.RolledBack, DynamicEndpointRevisionKind.Published, DynamicEndpointRevisionKind.Updated, DynamicEndpointRevisionKind.Created],
                (await host.Manager.GetHistoryAsync(id)).Select(r => r.Kind));

            Assert.True(await host.Manager.DeleteAsync(id));
        }

        await using var context = CreateBundledContext();
        Assert.Empty(await context.DynamicEndpointRevisions.ToListAsync());
        Assert.Empty(await context.DynamicEndpointDrafts.ToListAsync());
    }

    [Fact]
    public async Task Upgrading_an_existing_database_starts_the_history_with_the_published_revision()
    {
        // A database of 0.3: only the first migration, one endpoint at revision 2.
        await using (var context = CreateBundledContext())
        {
            await context.GetService<IMigrator>().MigrateAsync(InitialDynamicEndpoints.Id);
            var definition = DynamicEndpoint.Get("/upgraded").HandledBy("echo").Build() with
            {
                Id = Guid.NewGuid(),
                Revision = 2,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            context.DynamicEndpoints.Add(new DynamicEndpointRecord
            {
                Id = definition.Id,
                Method = "GET",
                Route = definition.Route,
                Enabled = true,
                Version = 2,
                CreatedAt = definition.CreatedAt,
                UpdatedAt = definition.UpdatedAt,
                Definition = System.Text.Json.JsonSerializer.Serialize(definition, DynamicEndpointsJson.SerializerOptions),
            });
            await context.SaveChangesAsync();
        }

        await using var host = await StartEfAsync();
        var id = (await host.Manager.ListAsync()).Single().Definition.Id;
        Assert.Equal(2, Assert.Single(await host.Manager.GetHistoryAsync(id)).Revision);

        await host.Manager.SetEnabledAsync(id, false);

        Assert.Equal([3, 2], (await host.Manager.GetHistoryAsync(id)).Select(r => r.Revision));
        await using var check = CreateBundledContext();
        Assert.Equal(2, await check.DynamicEndpointRevisions.CountAsync());
        Assert.Equal("/upgraded", (await host.Manager.GetRevisionAsync(id, 2))!.Definition.Route);
    }

    [Fact]
    public async Task A_publish_joins_the_applications_unit_of_work()
    {
        await using var host = await StartEfAsync();
        var draft = await host.Manager.SaveDraftAsync(DynamicEndpoint.Get("/ef-unit/v1").HandledBy("echo"));

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DynamicEndpointsDbContext>();
            var changes = host.Manager.BeginChanges(db.GetDynamicEndpointStore());
            await changes.PublishAsync(draft.EndpointId);
            // dropped without SaveChanges – nothing happened
        }

        Assert.NotNull(await host.Manager.GetDraftAsync(draft.EndpointId));
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/ef-unit/v1")).StatusCode);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DynamicEndpointsDbContext>();
            var changes = host.Manager.BeginChanges(db.GetDynamicEndpointStore());
            await changes.PublishAsync(draft.EndpointId);
            await db.SaveChangesAsync();
            await changes.ApplyAsync();
        }

        Assert.Null(await host.Manager.GetDraftAsync(draft.EndpointId));
        Assert.Equal(DynamicEndpointRevisionKind.Published, Assert.Single(await host.Manager.GetHistoryAsync(draft.EndpointId)).Kind);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/ef-unit/v1")).StatusCode);
    }

    [Fact]
    public void Diffs_address_named_items_by_name()
    {
        var before = DynamicEndpoint.Post("/diff").HandledBy("echo", new { greeting = "Hi" })
            .FromBody("a", p => p.Integer())
            .FromBody("b", p => p.Required())
            .WithRule("""{ "!!": [{ "var": "a" }] }""", "A is required.")
            .Build();
        var after = DynamicEndpoint.Post("/diff").HandledBy("echo", new { greeting = "Hello" })
            .FromBody("c")
            .FromBody("b", p => p.Required().MaxLength(3))
            .Build();

        var diff = DynamicEndpointDiff.Compare(before, after).ToDictionary(d => d.Path);

        Assert.Equal(DynamicEndpointDifferenceKind.Changed, diff["processorConfig.greeting"].Kind);
        Assert.Equal(DynamicEndpointDifferenceKind.Removed, diff["parameters[a]"].Kind);
        Assert.Equal(DynamicEndpointDifferenceKind.Added, diff["parameters[c]"].Kind);
        Assert.Equal(3, diff["parameters[b].maxLength"].To!.GetValue<int>());
        Assert.Equal(DynamicEndpointDifferenceKind.Removed, diff["rules"].Kind);
        Assert.Equal(5, diff.Count);
        Assert.Empty(DynamicEndpointDiff.Compare(before, before with { Revision = 9, UpdatedAt = DateTimeOffset.UtcNow }));
    }

    private Task<TestHost> StartEfAsync() =>
        TestHost.StartAsync(configure: b => b.UseEntityFrameworkStore(o => o.UseSqlite(ConnectionString), migrateOnStartup: true));

    private DynamicEndpointsDbContext CreateBundledContext() =>
        new(new DbContextOptionsBuilder<DynamicEndpointsDbContext>().UseSqlite(ConnectionString).Options);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
    }

    /// <summary>A custom store written before history existed.</summary>
    private sealed class PlainStore : IDynamicEndpointStore
    {
        private static readonly Dictionary<Guid, DynamicEndpointDefinition> Definitions = [];

        public Task<IReadOnlyList<DynamicEndpointDefinition>> GetAllAsync(CancellationToken cancellationToken)
        {
            lock (Definitions)
            {
                return Task.FromResult<IReadOnlyList<DynamicEndpointDefinition>>(Definitions.Values.Where(d => d.Route.StartsWith("/plain")).ToList());
            }
        }

        public Task<DynamicEndpointDefinition?> FindAsync(Guid id, CancellationToken cancellationToken)
        {
            lock (Definitions)
            {
                return Task.FromResult(Definitions.GetValueOrDefault(id));
            }
        }

        public Task AddAsync(DynamicEndpointDefinition definition, CancellationToken cancellationToken)
        {
            lock (Definitions)
            {
                Definitions[definition.Id] = definition;
            }

            return Task.CompletedTask;
        }

        public Task UpdateAsync(DynamicEndpointDefinition definition, int expectedRevision, CancellationToken cancellationToken) =>
            AddAsync(definition, cancellationToken);

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
        {
            lock (Definitions)
            {
                return Task.FromResult(Definitions.Remove(id));
            }
        }
    }
}
