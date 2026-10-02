using System.Net;
using DynamicEndpoints.EntityFrameworkCore;
using DynamicEndpoints.EntityFrameworkCore.Migrations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicEndpoints.Tests;

public sealed class EntityFrameworkTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"dynamic-endpoints-ef-{Guid.NewGuid():N}.db");

    private string ConnectionString => $"Data Source={_databasePath};Pooling=False";

    [Fact]
    public async Task Bundled_migrations_create_the_table_and_match_the_model()
    {
        await using (var host = await TestHost.StartAsync(configure: b => b.UseEntityFrameworkStore(o => o.UseSqlite(ConnectionString), migrateOnStartup: true)))
        {
            await host.Manager.CreateAsync(DynamicEndpoint.Get("/migrated").HandledBy("echo"));
            Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/migrated")).StatusCode);
        }

        await using var context = CreateBundledContext();
        Assert.Equal([InitialDynamicEndpoints.Id, DynamicEndpointRevisionsAndDrafts.Id], await context.Database.GetAppliedMigrationsAsync());
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Single(await context.DynamicEndpoints.ToListAsync());
    }

    [Fact]
    public async Task A_table_created_with_EnsureCreated_is_adopted()
    {
        await using (var context = CreateBundledContext())
        {
            await context.Database.EnsureCreatedAsync();
        }

        await using (var first = await TestHost.StartAsync(sqlitePath: _databasePath))
        {
            await first.Manager.CreateAsync(DynamicEndpoint.Get("/kept").HandledBy("echo"));
        }

        await using var host = await TestHost.StartAsync(configure: b => b.UseEntityFrameworkStore(o => o.UseSqlite(ConnectionString), migrateOnStartup: true));

        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/kept")).StatusCode);
        await using var check = CreateBundledContext();
        Assert.Equal([InitialDynamicEndpoints.Id, DynamicEndpointRevisionsAndDrafts.Id], await check.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task Changes_are_saved_by_the_applications_own_SaveChanges()
    {
        await using var app = await StartAppAsync();
        var manager = app.Services.GetRequiredService<IDynamicEndpointManager>();
        var client = app.GetTestClient();

        // Rolled back: nothing is saved, nothing is routed.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FeatureDbContext>();
            var changes = manager.BeginChanges(db.GetDynamicEndpointStore());
            await changes.CreateAsync(DynamicEndpoint.Get("/feature/rolled-back").HandledBy("echo"));
            db.Features.Add(new Feature { Name = "rolled back" });
            // no SaveChanges – the unit of work is dropped
        }

        Assert.Empty(await manager.ListAsync());

        // Committed: both rows in one SaveChanges, then the route goes live.
        Guid id;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FeatureDbContext>();
            var changes = manager.BeginChanges(db.GetDynamicEndpointStore());
            id = (await changes.UpsertAsync(DynamicEndpoint.Get("/feature/v1").HandledBy("echo"))).Id;
            db.Features.Add(new Feature { Name = "v1", EndpointId = id });
            await db.SaveChangesAsync();

            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/feature/v1")).StatusCode);
            await changes.ApplyAsync();
        }

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/feature/v1")).StatusCode);

        // Update and delete in the same way – a stale revision fails in SaveChanges, inside the application's transaction.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FeatureDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            var changes = manager.BeginChanges(scope.ServiceProvider); // the registered store shares this DbContext
            await changes.SetEnabledAsync(id, false);
            (await db.Features.SingleAsync()).Name = "v1 (disabled)";
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            await changes.ApplyAsync();
        }

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/feature/v1")).StatusCode);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FeatureDbContext>();
            Assert.Equal("v1 (disabled)", (await db.Features.SingleAsync()).Name);
            Assert.Equal(2, (await db.Set<DynamicEndpointRecord>().SingleAsync()).Version);

            var changes = manager.BeginChanges(db.GetDynamicEndpointStore());
            Assert.True(await changes.DeleteAsync(id));
            await db.SaveChangesAsync();
            await changes.ApplyAsync();
        }

        Assert.Empty(await manager.ListAsync());
    }

    [Fact]
    public async Task Concurrent_changes_fail_in_the_applications_SaveChanges()
    {
        await using var app = await StartAppAsync();
        var manager = app.Services.GetRequiredService<IDynamicEndpointManager>();
        var created = await manager.CreateAsync(DynamicEndpoint.Get("/race").HandledBy("echo"));

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FeatureDbContext>();
        var changes = manager.BeginChanges(db.GetDynamicEndpointStore());
        await changes.UpdateAsync(created with { Name = "mine" });

        await manager.UpdateAsync(created with { Name = "theirs" }); // another admin is faster

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync());
    }

    private DynamicEndpointsDbContext CreateBundledContext() =>
        new(new DbContextOptionsBuilder<DynamicEndpointsDbContext>().UseSqlite(ConnectionString).Options);

    private async Task<WebApplication> StartAppAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddDbContext<FeatureDbContext>(o => o.UseSqlite(ConnectionString));
        builder.Services.AddDynamicEndpoints()
            .AddProcessor("echo", r => Results.Ok(r.Parameters))
            .UseEntityFrameworkStore<FeatureDbContext>();
        var app = builder.Build();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<FeatureDbContext>().Database.EnsureCreatedAsync();
        }

        app.MapDynamicEndpoints();
        await app.StartAsync();
        return app;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
    }
}

public sealed class Feature
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public Guid? EndpointId { get; set; }
}

public sealed class FeatureDbContext(DbContextOptions<FeatureDbContext> options) : DbContext(options)
{
    public DbSet<Feature> Features => Set<Feature>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfiguration(new DynamicEndpointRecordConfiguration());
}
