using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DynamicEndpoints.Tests;

public sealed class TenancyTests
{
    private static Task<TestHost> StartAsync(Action<DynamicEndpointsTenancyBuilder> tenancy, string? sqlitePath = null, Action<WebApplication>? configureApp = null) =>
        TestHost.StartAsync(sqlitePath, configure: b => b
                .UseMultiTenancy(tenancy)
                .AddProcessor("tenant", request => Results.Ok(new { tenant = request.Tenant, endpoint = request.Endpoint.Tenant })),
            configureApp: configureApp);

    private static async Task<HttpResponseMessage> GetAsync(TestHost host, string url, string? tenant)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (tenant is not null)
        {
            request.Headers.Add("X-Tenant-Id", tenant);
        }

        return await host.Client.SendAsync(request);
    }

    [Fact]
    public async Task Tenants_share_a_route_and_each_gets_its_own_endpoint()
    {
        await using var host = await StartAsync(t => t.FromHeader());
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/greet/{name}").HandledBy<GreetingProcessor>(new { greeting = "Hi" }).FromRoute("name").Build() with { Tenant = "acme" });
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/greet/{name}").HandledBy<GreetingProcessor>(new { greeting = "Ahoy" }).FromRoute("name").Build() with { Tenant = "globex" });

        Assert.Equal("Hi, Ann!", await (await GetAsync(host, "/greet/Ann", "acme")).Content.ReadAsStringAsync());
        Assert.Equal("Ahoy, Ann!", await (await GetAsync(host, "/greet/Ann", "GLOBEX")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(host, "/greet/Ann", "initech")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(host, "/greet/Ann", null)).StatusCode);
    }

    [Fact]
    public async Task Shared_endpoints_serve_every_tenant_and_see_the_resolved_tenant()
    {
        await using var host = await StartAsync(t => t.FromHeader());
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/whoami").HandledBy("tenant"));

        var acme = await (await GetAsync(host, "/whoami", "acme")).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("acme", (string?)acme!["tenant"]);
        Assert.Null(acme["endpoint"]);

        var none = await (await GetAsync(host, "/whoami", null)).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Null(none!["tenant"]);
    }

    [Fact]
    public async Task Routes_conflict_within_a_tenant_and_with_shared_endpoints_only()
    {
        await using var host = await StartAsync(t => t.FromHeader());
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/orders").HandledBy("echo").Build() with { Tenant = "acme" });

        await host.Manager.CreateAsync(DynamicEndpoint.Get("/orders").HandledBy("echo").Build() with { Tenant = "globex" });
        var sameTenant = await Assert.ThrowsAsync<DynamicEndpointValidationException>(() =>
            host.Manager.CreateAsync(DynamicEndpoint.Get("/Orders").HandledBy("echo").Build() with { Tenant = "ACME" }));
        Assert.Contains("tenant 'acme'", sameTenant.Errors["route"][0]);
        await Assert.ThrowsAsync<DynamicEndpointValidationException>(() => host.Manager.CreateAsync(DynamicEndpoint.Get("/orders").HandledBy("echo")));
    }

    [Fact]
    public async Task Tenants_are_validated()
    {
        await using (var host = await TestHost.StartAsync())
        {
            var result = await host.Manager.ValidateAsync(DynamicEndpoint.Get("/t").HandledBy("echo").Build() with { Tenant = "acme" });
            Assert.Contains("UseMultiTenancy", result.Errors["tenant"][0]);
        }

        await using (var host = await StartAsync(t => t.FromHeader()))
        {
            var result = await host.Manager.ValidateAsync(DynamicEndpoint.Get("/t").HandledBy("echo").Build() with { Tenant = "a/b" });
            Assert.False(result.IsValid);
            Assert.True(result.Errors.ContainsKey("tenant"));
        }
    }

    [Fact]
    public async Task A_tenant_scoped_manager_sees_and_changes_only_its_own_endpoints()
    {
        await using var host = await StartAsync(t => t.FromHeader());
        var acme = host.Manager.ForTenant("acme");
        var globex = host.Manager.ForTenant("globex");

        var created = await acme.CreateAsync(DynamicEndpoint.Get("/mine").HandledBy("echo"));
        Assert.Equal("acme", created.Tenant);
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/shared").HandledBy("echo"));

        Assert.Equal(["/mine"], (await acme.ListAsync()).Select(s => s.Definition.Route));
        Assert.Empty(await globex.ListAsync());
        Assert.Equal(["/shared"], (await host.Manager.ForTenant(null).ListAsync()).Select(s => s.Definition.Route));
        Assert.Null(await globex.GetAsync(created.Id));

        await Assert.ThrowsAsync<DynamicEndpointNotFoundException>(() => globex.UpdateAsync(created with { Name = "stolen" }));
        await Assert.ThrowsAsync<DynamicEndpointNotFoundException>(() => globex.SetEnabledAsync(created.Id, false));
        await Assert.ThrowsAsync<DynamicEndpointValidationException>(() => globex.UpsertAsync(created with { Name = "stolen" }));
        Assert.False(await globex.DeleteAsync(created.Id));

        // A scoped manager can't hand out endpoints to another tenant, and can't escape its scope either.
        var moved = await acme.UpdateAsync(created with { Tenant = "globex", Name = "renamed" });
        Assert.Equal(("acme", "renamed"), (moved.Tenant, moved.Name));
        Assert.Empty(await acme.ForTenant("globex").ListAsync());

        Assert.True(await acme.DeleteAsync(created.Id));
    }

    [Fact]
    public async Task Tenant_change_sets_assign_the_tenant_and_reject_foreign_endpoints()
    {
        await using var host = await StartAsync(t => t.FromHeader());
        var foreign = await host.Manager.CreateAsync(DynamicEndpoint.Get("/foreign").HandledBy("echo").Build() with { Tenant = "globex" });

        var changes = host.Manager.ForTenant("acme").BeginChanges(host.Services);
        var created = await changes.CreateAsync(DynamicEndpoint.Get("/staged").HandledBy("echo"));
        await Assert.ThrowsAsync<DynamicEndpointNotFoundException>(() => changes.SetEnabledAsync(foreign.Id, false));
        await changes.ApplyAsync();

        Assert.Equal("acme", created.Tenant);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(host, "/staged", "acme")).StatusCode);

        var store = host.Services.GetRequiredService<IDynamicEndpointStore>().ForTenant("acme");
        Assert.Equal(["/staged"], (await store.GetAllAsync(default)).Select(d => d.Route));
        await Assert.ThrowsAsync<DynamicEndpointValidationException>(() => store.AddAsync(foreign with { Id = Guid.NewGuid() }, default));
    }

    [Fact]
    public async Task Route_prefix_puts_every_endpoint_under_the_tenant_segment()
    {
        await using var host = await StartAsync(t => t.FromRoutePrefix("/t/{tenant}"),
            configureApp: app => app.MapDynamicEndpointsOpenApi("/openapi/{tenant}/dynamic.json"));
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/whoami").HandledBy("tenant").Build() with { Tenant = "acme" });
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/everyone").HandledBy("tenant"));

        var acme = await host.Client.GetFromJsonAsync<JsonObject>("/t/acme/whoami");
        Assert.Equal(("acme", "acme"), ((string?)acme!["tenant"], (string?)acme["endpoint"]));
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/t/globex/whoami")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/whoami")).StatusCode);
        Assert.Equal("globex", (string?)(await host.Client.GetFromJsonAsync<JsonObject>("/t/globex/everyone"))!["tenant"]);

        // The tenant's document has its endpoints with the tenant filled in, the shared one documents the parameter.
        var tenantDocument = await host.Client.GetFromJsonAsync<JsonObject>("/openapi/acme/dynamic.json");
        Assert.Equal(["/t/acme/everyone", "/t/acme/whoami"], tenantDocument!["paths"]!.AsObject().Select(p => p.Key).Order());
        Assert.Null(tenantDocument["paths"]!["/t/acme/everyone"]!["get"]!["parameters"]);

        var sharedDocument = await host.Client.GetFromJsonAsync<JsonObject>("/openapi/dynamic.json");
        var shared = Assert.Single(sharedDocument!["paths"]!.AsObject());
        Assert.Equal("/t/{tenant}/everyone", shared.Key);
        Assert.Equal("tenant", (string?)shared.Value!["get"]!["parameters"]![0]!["name"]);
    }

    [Fact]
    public async Task Host_and_claim_resolvers_find_the_tenant()
    {
        await using (var host = await StartAsync(t => t.FromHost()))
        {
            await host.Manager.CreateAsync(DynamicEndpoint.Get("/host").HandledBy("tenant").Build() with { Tenant = "acme" });
            using var request = new HttpRequestMessage(HttpMethod.Get, "http://acme.example.com/host");
            Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(request)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("http://example.com/host")).StatusCode);
        }

        await using (var host = await TestHost.StartAsync(configure: b =>
        {
            b.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TenantClaimHandler>("test", null);
            b.UseMultiTenancy(t => t.FromClaim()).AddProcessor("tenant", request => Results.Ok(request.Tenant));
        }))
        {
            await host.Manager.CreateAsync(DynamicEndpoint.Get("/claim").HandledBy("tenant").Build() with { Tenant = "acme" });
            using var acme = new HttpRequestMessage(HttpMethod.Get, "/claim") { Headers = { { "X-Test-Tenant", "acme" } } };
            Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(acme)).StatusCode);
            using var globex = new HttpRequestMessage(HttpMethod.Get, "/claim") { Headers = { { "X-Test-Tenant", "globex" } } };
            Assert.Equal(HttpStatusCode.NotFound, (await host.Client.SendAsync(globex)).StatusCode);
        }
    }

    [Fact]
    public async Task Tenant_admin_api_is_scoped_to_its_tenant_and_the_global_one_filters()
    {
        await using var host = await StartAsync(t => t.FromHeader(),
            configureApp: app => app.MapDynamicEndpointsTenantAdmin("/admin/tenants/{tenant}/endpoints"));
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/other").HandledBy("echo").Build() with { Tenant = "globex" });

        var response = await host.Client.PostAsJsonAsync("/admin/tenants/acme/endpoints",
            new { method = "GET", route = "/admin-made", processor = "echo", tenant = "globex" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<DynamicEndpointDefinition>(DynamicEndpointsJson.SerializerOptions);
        Assert.Equal("acme", created!.Tenant);
        Assert.Equal($"/admin/tenants/acme/endpoints/{created.Id}", response.Headers.Location!.OriginalString);

        var list = await host.Client.GetFromJsonAsync<JsonArray>("/admin/tenants/acme/endpoints");
        Assert.Equal("/admin-made", (string?)Assert.Single(list!)!["definition"]!["route"]);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync($"/admin/tenants/globex/endpoints/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.DeleteAsync($"/admin/tenants/globex/endpoints/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/admin/tenants/no%20tenant/endpoints")).StatusCode);

        Assert.Equal(2, (await host.Client.GetFromJsonAsync<JsonArray>("/admin/endpoints"))!.Count);
        Assert.Single((await host.Client.GetFromJsonAsync<JsonArray>("/admin/endpoints?tenant=globex"))!);
        Assert.Equal(["acme", "globex"], (await host.Client.GetFromJsonAsync<string[]>("/admin/endpoints/tenants"))!);

        // Dynamic endpoints can't take the tenant admin prefix.
        await Assert.ThrowsAsync<DynamicEndpointValidationException>(() => host.Manager.CreateAsync(DynamicEndpoint.Get("/admin/tenants/x").HandledBy("echo")));
    }

    [Fact]
    public async Task Drafts_and_history_of_a_tenant_scoped_manager_stay_within_the_tenant()
    {
        await using var host = await StartAsync(t => t.FromHeader());
        var foreign = await host.Manager.CreateAsync(DynamicEndpoint.Get("/foreign").HandledBy("echo").Build() with { Tenant = "globex" });
        var acme = host.Manager.ForTenant("acme");

        var draft = await acme.SaveDraftAsync(new DynamicEndpointDraft { Definition = DynamicEndpoint.Get("/drafted").HandledBy("echo") });
        Assert.Equal("acme", draft.Definition.Tenant);
        Assert.Single(await acme.ListDraftsAsync());
        Assert.Empty(await host.Manager.ForTenant("globex").ListDraftsAsync());
        Assert.Null(await host.Manager.ForTenant("globex").GetDraftAsync(draft.EndpointId));
        Assert.False(await host.Manager.ForTenant("globex").DiscardDraftAsync(draft.EndpointId));

        // Another tenant's endpoint can't be drafted over, and its history doesn't exist for this tenant.
        await Assert.ThrowsAsync<DynamicEndpointValidationException>(() =>
            acme.SaveDraftAsync(new DynamicEndpointDraft { Definition = DynamicEndpoint.Get("/foreign").HandledBy("echo").Build() with { Id = foreign.Id } }));
        await Assert.ThrowsAsync<DynamicEndpointNotFoundException>(() => acme.GetHistoryAsync(foreign.Id));
        Assert.Null(await acme.GetRevisionAsync(foreign.Id, 1));
        await Assert.ThrowsAsync<DynamicEndpointNotFoundException>(() => acme.RollbackAsync(foreign.Id, 1));
        await Assert.ThrowsAsync<DynamicEndpointNotFoundException>(() => host.Manager.ForTenant("globex").PublishAsync(draft.EndpointId));

        var published = await acme.PublishAsync(draft.EndpointId);
        Assert.Equal("acme", published.Tenant);
        Assert.Single(await acme.GetHistoryAsync(published.Id));
        Assert.Equal("globex", (await host.Manager.GetAsync(foreign.Id))!.Definition.Tenant);
    }

    [Fact]
    public async Task Tenant_change_sets_assign_the_tenant_to_drafts()
    {
        await using var host = await StartAsync(t => t.FromHeader());
        var changes = host.Manager.ForTenant("acme").BeginChanges(host.Services);
        var draft = await changes.SaveDraftAsync(new DynamicEndpointDraft { Definition = DynamicEndpoint.Get("/staged").HandledBy("echo") });
        await changes.ApplyAsync();

        Assert.Equal("acme", draft.Definition.Tenant);
        Assert.Equal("acme", (await host.Manager.GetDraftAsync(draft.EndpointId))!.Definition.Tenant);
    }

    [Fact]
    public async Task Tenant_admin_api_exports_and_syncs_only_its_own_endpoints()
    {
        await using var host = await StartAsync(t => t.FromHeader(),
            configureApp: app => app.MapDynamicEndpointsTenantAdmin("/admin/tenants/{tenant}/endpoints"));
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/mine").HandledBy("echo").Build() with { Tenant = "acme" });
        var foreign = await host.Manager.CreateAsync(DynamicEndpoint.Get("/theirs").HandledBy("echo").Build() with { Tenant = "globex" });

        var export = await host.Client.GetFromJsonAsync<JsonObject>("/admin/tenants/acme/endpoints/export");
        Assert.Equal("/mine", (string?)Assert.Single(export!["endpoints"]!.AsArray())!["route"]);

        // Syncing an empty file deletes the tenant's endpoints – and nobody else's.
        var empty = new DynamicEndpointExport { Endpoints = [DynamicEndpoint.Get("/new").HandledBy("echo")] };
        var response = await host.Client.PostAsync("/admin/tenants/acme/endpoints/import?mode=sync",
            new StringContent(empty.ToJson(), System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var acme = await host.Manager.ForTenant("acme").ListAsync();
        Assert.Equal("/new", Assert.Single(acme).Definition.Route);
        Assert.NotNull(await host.Manager.GetAsync(foreign.Id));
    }

    [Fact]
    public async Task Output_cache_keeps_the_responses_of_tenants_on_the_same_route_apart()
    {
        await using var host = await TestHost.StartAsync(configure: b =>
            {
                b.UseMultiTenancy(t => t.FromHeader());
                b.Services.AddOutputCache();
            },
            configureApp: app => app.UseOutputCache());
        var caching = new DynamicEndpointCaching { OutputCacheSeconds = 60 };
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/greet/{name}").HandledBy<GreetingProcessor>(new { greeting = "Hi" })
            .FromRoute("name").WithCaching(caching).Build() with { Tenant = "acme" });
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/greet/{name}").HandledBy<GreetingProcessor>(new { greeting = "Ahoy" })
            .FromRoute("name").WithCaching(caching).Build() with { Tenant = "globex" });

        Assert.Equal("Hi, Ann!", await (await GetAsync(host, "/greet/Ann", "acme")).Content.ReadAsStringAsync());
        Assert.Equal("Ahoy, Ann!", await (await GetAsync(host, "/greet/Ann", "globex")).Content.ReadAsStringAsync());
        Assert.Equal("Hi, Ann!", await (await GetAsync(host, "/greet/Ann", "acme")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Tenants_are_persisted_without_a_schema_change()
    {
        var path = Path.Combine(Path.GetTempPath(), $"de-tenancy-{Guid.NewGuid():N}.db");
        try
        {
            Guid id;
            await using (var host = await StartAsync(t => t.FromHeader(), path))
            {
                id = (await host.Manager.CreateAsync(DynamicEndpoint.Get("/persisted").HandledBy("tenant").Build() with { Tenant = "acme" })).Id;
            }

            await using (var host = await StartAsync(t => t.FromHeader(), path))
            {
                Assert.Equal("acme", (await host.Manager.GetAsync(id))!.Definition.Tenant);
                Assert.Equal(HttpStatusCode.OK, (await GetAsync(host, "/persisted", "acme")).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(host, "/persisted", "globex")).StatusCode);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class TenantClaimHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers["X-Test-Tenant"].FirstOrDefault() is not { } tenant)
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity([new Claim("tenant_id", tenant)], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
