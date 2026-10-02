using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using DynamicEndpoints.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DynamicEndpoints.Tests;

public sealed class AuditTests
{
    private static Task<TestHost> StartAsync(Action<IDynamicEndpointsBuilder> configure, Action<WebApplication>? configureApp = null) =>
        TestHost.StartAsync(configure: b =>
        {
            b.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestUserHandler>("test", null);
            configure(b);
        }, configureApp: configureApp);

    private static HttpRequestMessage As(string user, HttpMethod method, string url, object? body = null) => new(method, url)
    {
        Headers = { { "X-Test-User", user } },
        Content = body is null ? null : JsonContent.Create(body),
    };

    [Fact]
    public async Task Local_changes_are_audited_with_user_and_diff()
    {
        var log = new InMemoryDynamicEndpointAuditLog();
        await using var host = await StartAsync(b => b.AddAuditLog(a => a.To(log)),
            configureApp: app => app.UseAuthentication());

        var response = await host.Client.SendAsync(As("alice", HttpMethod.Post, "/admin/endpoints",
            new { method = "GET", route = "/audited", processor = "echo", parameters = new[] { new { name = "q", maxLength = 5 } } }));
        var created = (await response.Content.ReadFromJsonAsync<DynamicEndpointDefinition>(DynamicEndpointsJson.SerializerOptions))!;

        var changed = created with
        {
            Name = "Audited",
            Parameters = [created.Parameters[0] with { MaxLength = 10 }],
        };
        Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(As("bob", HttpMethod.Put, $"/admin/endpoints/{created.Id}", changed))).StatusCode);
        await host.Manager.DeleteAsync(created.Id);   // outside a request: no user

        Assert.Collection(log.Entries,
            e =>
            {
                Assert.Equal((DynamicEndpointChangeKind.Created, "alice", 1, "/audited"), (e.Kind, e.User, e.Revision, e.Route));
                Assert.Contains(e.Changes, c => c.Path == "route" && c.Before is null && (string?)c.After == "/audited");
                Assert.Null(e.Previous);
                Assert.NotNull(e.Definition);
            },
            e =>
            {
                Assert.Equal((DynamicEndpointChangeKind.Updated, "bob", 2), (e.Kind, e.User, e.Revision));
                Assert.Equal(["name", "parameters[0].maxLength"], e.Changes.Select(c => c.Path).Order());
                var maxLength = e.Changes.Single(c => c.Path == "parameters[0].maxLength");
                Assert.Equal((5, 10), ((int)maxLength.Before!, (int)maxLength.After!));
            },
            e =>
            {
                Assert.Equal((DynamicEndpointChangeKind.Deleted, (string?)null, 2), (e.Kind, e.User, e.Revision));
                Assert.Null(e.Definition);
            });
    }

    [Fact]
    public async Task Remote_changes_are_not_audited()
    {
        var log = new InMemoryDynamicEndpointAuditLog();
        var store = new InMemoryDynamicEndpointStore();
        await using var host = await StartAsync(b =>
        {
            b.Services.Replace(ServiceDescriptor.Singleton<IDynamicEndpointStore>(store));
            b.AddAuditLog(a => a.To(log));
        });

        // Written by "another instance" and picked up by a reload.
        await store.AddAsync(new DynamicEndpointDefinition { Id = Guid.NewGuid(), Route = "/remote", Processor = "echo", Revision = 1 }, default);
        await host.Manager.ReloadAsync();
        await host.Manager.ReloadAsync();

        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/remote")).StatusCode);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task Admin_api_queries_the_audit_log_and_a_tenant_sees_only_its_own()
    {
        await using var host = await StartAsync(b => b.UseMultiTenancy(t => t.FromHeader()).AddAuditLog(a => a.ToMemory()),
            configureApp: app => app.MapDynamicEndpointsTenantAdmin("/admin/tenants/{tenant}/endpoints"));
        var acme = await host.Manager.ForTenant("acme").CreateAsync(DynamicEndpoint.Get("/a").HandledBy("echo"));
        await host.Manager.ForTenant("acme").SetEnabledAsync(acme.Id, false);
        await host.Manager.ForTenant("globex").CreateAsync(DynamicEndpoint.Get("/g").HandledBy("echo"));

        var all = await host.Client.GetFromJsonAsync<JsonArray>("/admin/endpoints/audit");
        Assert.Equal(["Created", "Updated", "Created"], all!.Select(e => (string?)e!["kind"]));   // newest first
        Assert.Equal("globex", (string?)all![0]!["tenant"]);

        var single = await host.Client.GetFromJsonAsync<JsonArray>($"/admin/endpoints/{acme.Id}/audit?limit=1");
        Assert.Equal("enabled", (string?)Assert.Single(single!)!["changes"]![0]!["path"]);

        var tenant = await host.Client.GetFromJsonAsync<JsonArray>("/admin/tenants/acme/endpoints/audit?tenant=globex");
        Assert.Equal(2, tenant!.Count);
        Assert.All(tenant, e => Assert.Equal("acme", (string?)e!["tenant"]));
    }

    [Fact]
    public async Task Without_a_queryable_sink_entries_go_to_the_logger_and_the_audit_api_is_404()
    {
        var logs = new ConcurrentQueue<string>();
        await using var host = await StartAsync(b =>
        {
            b.Services.AddSingleton<ILoggerProvider>(new CollectingLoggerProvider(logs));
            b.AddAuditLog();
        });

        await host.Manager.CreateAsync(DynamicEndpoint.Get("/logged").Named("Logged").HandledBy("echo"));

        Assert.Contains(logs, l => l.StartsWith("DynamicEndpoints.Audit: Dynamic endpoint Created GET /logged", StringComparison.Ordinal) &&
            l.Contains("name: – → \"Logged\""));
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/admin/endpoints/audit")).StatusCode);
    }

    [Fact]
    public async Task Entity_framework_sink_persists_and_filters_entries()
    {
        var path = Path.Combine(Path.GetTempPath(), $"de-audit-{Guid.NewGuid():N}.db");
        try
        {
            await using (var db = new AuditDbContext(new DbContextOptionsBuilder<AuditDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options))
            {
                await db.Database.EnsureCreatedAsync();
            }

            await using var host = await StartAsync(b =>
            {
                b.Services.AddDbContext<AuditDbContext>(o => o.UseSqlite($"Data Source={path};Pooling=False"));
                b.UseEntityFrameworkStore<AuditDbContext>().AddAuditLog(a => a.ToEntityFramework<AuditDbContext>());
            });
            var first = await host.Manager.CreateAsync(DynamicEndpoint.Get("/ef/a").HandledBy("echo"));
            var since = DateTimeOffset.UtcNow;
            await host.Manager.UpdateAsync(first with { Name = "renamed" });
            await host.Manager.CreateAsync(DynamicEndpoint.Get("/ef/b").HandledBy("echo"));

            var log = host.Services.CreateScope().ServiceProvider.GetRequiredService<IDynamicEndpointAuditLog>();
            var all = await log.QueryAsync(new DynamicEndpointAuditQuery(), default);
            Assert.Equal(["/ef/b", "/ef/a", "/ef/a"], all.Select(e => e.Route));
            Assert.Equal("renamed", (string?)all[1].Changes.Single().After);

            var ofFirst = await log.QueryAsync(new DynamicEndpointAuditQuery { EndpointId = first.Id, From = since }, default);
            Assert.Equal(DynamicEndpointChangeKind.Updated, Assert.Single(ofFirst).Kind);
            Assert.Equal(2, (await host.Client.GetFromJsonAsync<JsonArray>($"/admin/endpoints/{first.Id}/audit"))!.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class AuditDbContext(DbContextOptions<AuditDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.ApplyDynamicEndpointsConfiguration().ApplyDynamicEndpointsAuditConfiguration();
    }

    private sealed class TestUserHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers["X-Test-User"].FirstOrDefault() is not { } user)
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, user)], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private sealed class CollectingLoggerProvider(ConcurrentQueue<string> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, logs);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<string> logs) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                logs.Enqueue($"{category}: {formatter(state, exception)}");
        }
    }
}
