using System.Diagnostics;
using System.Net;
using DynamicEndpoints.EntityFrameworkCore.Migrations;
using DynamicEndpoints.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace DynamicEndpoints.Tests;

/// <summary>Runs only where Docker is available (CI, developer machines with Docker); skipped elsewhere.</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            using var docker = Process.Start(new ProcessStartInfo("docker", "info")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            return docker is not null && docker.WaitForExit(15_000) && docker.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    });

    public DockerFactAttribute()
    {
        if (!Available.Value)
        {
            Skip = "Docker is not available.";
        }
    }
}

public sealed class IntegrationTests
{
    [DockerFact]
    public async Task PostgreSql_migrations_and_LISTEN_NOTIFY_propagate_changes_between_instances()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync();
        var connectionString = postgres.GetConnectionString();

        // The test server starts in memory; the EF store and the notifier configured here replace that.
        await using var a = await StartWithEntityFrameworkAsync(connectionString);
        await using var b = await StartWithEntityFrameworkAsync(connectionString);

        var created = await a.Manager.CreateAsync(DynamicEndpoint.Get("/pg/everywhere").HandledBy("echo"));
        await ChangeTests.Eventually(async () => (await b.Client.GetAsync("/pg/everywhere")).StatusCode == HttpStatusCode.OK);

        await a.Manager.DeleteAsync(created.Id);
        await ChangeTests.Eventually(async () => (await b.Client.GetAsync("/pg/everywhere")).StatusCode == HttpStatusCode.NotFound);

        await using var context = new DynamicEndpoints.EntityFrameworkCore.DynamicEndpointsDbContext(
            new DbContextOptionsBuilder<DynamicEndpoints.EntityFrameworkCore.DynamicEndpointsDbContext>().UseNpgsql(connectionString).Options);
        Assert.Equal([InitialDynamicEndpoints.Id], await context.Database.GetAppliedMigrationsAsync());
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [DockerFact]
    public async Task Redis_pub_sub_propagates_changes_between_instances()
    {
        await using var redis = new RedisBuilder("redis:7-alpine").Build();
        await redis.StartAsync();
        var store = new InMemoryDynamicEndpointStore();

        Task<DynamicEndpointsTestServer> Instance() => DynamicEndpointsTestServer.StartAsync(new DynamicEndpointsTestServerOptions
        {
            Store = store,
            DynamicEndpoints = b => b
                .AddProcessor("echo", r => Results.Ok(r.Parameters))
                .UseRedisChangeNotifications(redis.GetConnectionString()),
        });

        await using var a = await Instance();
        await using var b = await Instance();
        await Task.Delay(500); // subscriptions are set up in the background

        await a.Manager.CreateAsync(DynamicEndpoint.Get("/redis/everywhere").HandledBy("echo"));
        await ChangeTests.Eventually(async () => (await b.Client.GetAsync("/redis/everywhere")).StatusCode == HttpStatusCode.OK);
    }

    private static Task<DynamicEndpointsTestServer> StartWithEntityFrameworkAsync(string connectionString) =>
        DynamicEndpointsTestServer.StartAsync(new DynamicEndpointsTestServerOptions
        {
            DynamicEndpoints = b => b
                .AddProcessor("echo", r => Results.Ok(r.Parameters))
                .UseEntityFrameworkStore(o => o.UseNpgsql(connectionString), migrateOnStartup: true)
                .UsePostgreSqlChangeNotifications(connectionString),
        });
}

public sealed class TestingPackageTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"dynamic-endpoints-app-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task The_application_runs_with_in_memory_endpoints_and_test_processors()
    {
        var store = new InMemoryDynamicEndpointStore();
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Default", $"Data Source={_databasePath};Pooling=False"))
            .WithInMemoryDynamicEndpoints(b => b.AddProcessor("fake", _ => Results.Text("faked")), store);
        var client = factory.CreateClient();

        // The application's seeders still run – against the in-memory store.
        Assert.StartsWith("Hello, World!", await client.GetStringAsync("/hello/World"));

        await factory.AddDynamicEndpointAsync(DynamicEndpoint.Get("/test/fake").HandledBy("fake"));
        Assert.Equal("faked", await client.GetStringAsync("/test/fake"));
        Assert.Contains((await store.GetAllAsync(default)), d => d.Route == "/test/fake");
    }

    [Fact]
    public async Task The_test_server_runs_endpoints_without_an_application()
    {
        await using var server = await DynamicEndpointsTestServer.StartAsync(b => b.AddProcessor("hello", r => Results.Text($"Hi {r.Get<string>("name")}")));

        await server.AddEndpointAsync(DynamicEndpoint.Get("/hi/{name}").HandledBy("hello").FromRoute("name", p => p.MinLength(2)));

        Assert.Equal("Hi Ola", await server.Client.GetStringAsync("/hi/Ola"));
        Assert.Equal(HttpStatusCode.BadRequest, (await server.Client.GetAsync("/hi/O")).StatusCode);
        Assert.NotNull(server.OpenApi.GetDocument()["paths"]!["/hi/{name}"]);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
    }
}
