using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace DynamicEndpoints.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"dynamic-endpoints-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Endpoints_survive_an_application_restart()
    {
        Guid deletedId;
        await using (var first = await TestHost.StartAsync(_databasePath))
        {
            await first.Manager.CreateAsync(DynamicEndpoint.Get("/persisted/{name}")
                .HandledBy("greeting", new { greeting = "Welcome back" })
                .FromRoute("name", p => p.MinLength(2)));
            deletedId = (await first.Manager.CreateAsync(DynamicEndpoint.Get("/gone").HandledBy("echo"))).Id;
            await first.Manager.DeleteAsync(deletedId);
        }

        await using var second = await TestHost.StartAsync(_databasePath);

        Assert.Equal("Welcome back, Ola!", await second.Client.GetStringAsync("/persisted/Ola"));
        Assert.Equal(HttpStatusCode.BadRequest, (await second.Client.GetAsync("/persisted/O")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await second.Client.GetAsync("/gone")).StatusCode);
        var state = Assert.Single(await second.Manager.ListAsync());
        Assert.Equal(DynamicEndpointStatus.Active, state.Status);
    }

    [Fact]
    public async Task Seeders_get_the_manager_injected_and_run_after_persisted_definitions_are_loaded()
    {
        await using (var first = await TestHost.StartAsync(_databasePath, configure: b => b.AddSeeder<PingSeeder>()))
        {
            Assert.Equal(HttpStatusCode.OK, (await first.Client.GetAsync("/seeded")).StatusCode);
        }

        // Second start: the definition is already persisted – the seeder sees it and does not duplicate it.
        await using var second = await TestHost.StartAsync(_databasePath, configure: b => b.AddSeeder<PingSeeder>());
        Assert.Single(await second.Manager.ListAsync());
    }

    private sealed class PingSeeder(IDynamicEndpointManager manager) : IDynamicEndpointSeeder
    {
        public async Task SeedAsync(CancellationToken cancellationToken)
        {
            if ((await manager.ListAsync(cancellationToken)).Count == 0)
            {
                await manager.CreateAsync(DynamicEndpoint.Get("/seeded").HandledBy("echo"), cancellationToken);
            }
        }
    }

    [Fact]
    public async Task Changes_made_by_another_instance_are_picked_up_on_reload()
    {
        await using var a = await TestHost.StartAsync(_databasePath);
        await using var b = await TestHost.StartAsync(_databasePath);

        var created = await a.Manager.CreateAsync(DynamicEndpoint.Get("/shared").HandledBy("echo"));
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.GetAsync("/shared")).StatusCode);
        Assert.Equal(DynamicEndpointStatus.Pending, (await b.Manager.GetAsync(created.Id))!.Status);

        await b.Manager.ReloadAsync();
        Assert.Equal(HttpStatusCode.OK, (await b.Client.GetAsync("/shared")).StatusCode);

        // Stale write from the other instance is rejected by the database concurrency token.
        await a.Manager.SetEnabledAsync(created.Id, false);
        await Assert.ThrowsAsync<DynamicEndpointConcurrencyException>(() => b.Manager.UpdateAsync(created with { Route = "/shared2" }));
    }

    [Fact]
    public async Task Automatic_refresh_propagates_changes()
    {
        await using var a = await TestHost.StartAsync(_databasePath);
        await using var b = await TestHost.StartAsync(_databasePath, o => o.RefreshInterval = TimeSpan.FromMilliseconds(100));

        await a.Manager.CreateAsync(DynamicEndpoint.Get("/eventually").HandledBy("echo"));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        HttpStatusCode status;
        do
        {
            await Task.Delay(50);
            status = (await b.Client.GetAsync("/eventually")).StatusCode;
        }
        while (status != HttpStatusCode.OK && DateTime.UtcNow < deadline);

        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task Sample_application_serves_seeded_endpoints()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("ConnectionStrings:Default", $"Data Source={_databasePath};Pooling=False"));
        var client = factory.CreateClient();

        Assert.StartsWith("Hello, World!", await client.GetStringAsync("/hello/World"));
        var sum = await client.GetFromJsonAsync<JsonObject>("/calc/sum?a=2&b=40");
        Assert.Equal(42, sum!["result"]!.GetValue<decimal>());

        var note = await client.PostAsJsonAsync("/notes", new { title = "Test note", priority = 2 });
        Assert.Equal(HttpStatusCode.Created, note.StatusCode);
        var id = (await note.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
        Assert.Equal("Test note", (await client.GetFromJsonAsync<JsonObject>($"/notes/{id}"))!["title"]!.GetValue<string>());

        // Custom management API built on the injected IDynamicEndpointManager.
        var greeting = await client.PostAsJsonAsync("/api/greetings", new { slug = "pirate", greeting = "Ahoy" });
        Assert.Equal(HttpStatusCode.Created, greeting.StatusCode);
        Assert.Equal("Ahoy, Jack!", await client.GetStringAsync("/greetings/pirate/Jack"));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/greetings", new { slug = "pirate", greeting = "Again" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/greetings/pirate")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/greetings/pirate/Jack")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/openapi/dynamic.json")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/index.html")).StatusCode);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }
}
