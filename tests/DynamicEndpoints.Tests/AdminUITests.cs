using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DynamicEndpoints.Tests;

public sealed class AdminUITests
{
    [Fact]
    public async Task The_panel_is_served_from_the_assembly_with_its_configuration()
    {
        await using var host = await TestHost.StartAsync(configureApp: app => app.MapDynamicEndpointsAdminUI("/panel", "/admin/endpoints", o =>
        {
            o.Title = "Ops <panel>";
            o.SwaggerUrl = "/swagger";
        }));

        var page = await host.Client.GetAsync("/panel/");
        var html = await page.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", page.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("<title>Ops &lt;panel&gt;</title>", html);
        Assert.Contains("\"apiPath\":\"/admin/endpoints\"", html);
        Assert.Contains("\"swaggerUrl\":\"/swagger\"", html);
        Assert.Contains("\"openApiUrl\":null", html);
        Assert.DoesNotContain("<panel>", html); // the title is escaped in the JSON configuration as well
        Assert.Contains("src=\"/panel/admin.js?v=", html);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/panel")).StatusCode);

        var script = await host.Client.GetAsync("/panel/admin.js");
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Equal("text/javascript", script.Content.Headers.ContentType!.MediaType);
        var code = await script.Content.ReadAsStringAsync();
        foreach (var feature in new[] { "function openHistory", "function openImport", "function openOpenApiImport", "function openExport",
                     "function openAuditLog", "function renderCachingSection", "function renderRateLimitSection", "api('/info')" })
        {
            Assert.Contains(feature, code);
        }

        var cached = new HttpRequestMessage(HttpMethod.Get, "/panel/admin.js");
        cached.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(script.Headers.ETag!.ToString()));
        Assert.Equal(HttpStatusCode.NotModified, (await host.Client.SendAsync(cached)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/panel/admin.css")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/panel/secrets.json")).StatusCode);
    }

    [Fact]
    public async Task The_prefix_is_reserved_and_urls_follow_the_path_base()
    {
        await using var host = await TestHost.StartAsync(configureApp: app =>
        {
            app.UsePathBase("/base");
            app.MapDynamicEndpointsAdminUI("/panel", "/admin/endpoints", o => o.OpenApiUrl = "https://docs.example.com/openapi.json");
        });

        var html = await host.Client.GetStringAsync("/base/panel/");

        Assert.Contains("\"apiPath\":\"/base/admin/endpoints\"", html);
        Assert.Contains("\"pathBase\":\"/base\"", html);
        Assert.Contains("\"openApiUrl\":\"https://docs.example.com/openapi.json\"", html);
        Assert.Contains("href=\"/base/panel/admin.css?v=", html);
        var error = await Assert.ThrowsAsync<DynamicEndpointValidationException>(() =>
            host.Manager.CreateAsync(DynamicEndpoint.Get("/panel/hijack").HandledBy("echo")));
        Assert.Contains("route", error.Errors.Keys);
    }

    [Fact]
    public async Task The_panel_can_require_authorization()
    {
        await using var host = await TestHost.StartAsync(
            configure: b => b.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>("Test", null),
            configureApp: app => app.MapDynamicEndpointsAdminUI("/panel", "/admin/endpoints").RequireAuthorization());

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("/panel/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("/panel/admin.js")).StatusCode);

        var request = new HttpRequestMessage(HttpMethod.Get, "/panel/");
        request.Headers.Add("X-User", "ada");
        Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task A_tenant_panel_fills_its_route_values_into_the_api_path_and_links()
    {
        await using var host = await TestHost.StartAsync(
            configure: b => b.UseMultiTenancy(t => t.FromHeader()),
            configureApp: app =>
            {
                app.MapDynamicEndpointsTenantAdmin("/admin/tenants/{tenant}/endpoints");
                app.MapDynamicEndpointsAdminUI("/tenants/{tenant}/panel", "/admin/tenants/{tenant}/endpoints", o =>
                {
                    o.OpenApiUrl = "/openapi/{tenant}/dynamic.json";
                    o.TenantSwaggerUrl = "https://docs.example.com/{tenant}/swagger";
                });
                app.MapDynamicEndpointsAdminUI("/panel", "/admin/endpoints", o => o.TenantOpenApiUrl = "/openapi/{tenant}/dynamic.json");
            });

        var html = await host.Client.GetStringAsync("/tenants/acme%20corp/panel/");
        Assert.Contains("\"apiPath\":\"/admin/tenants/acme%20corp/endpoints\"", html);
        Assert.Contains("\"openApiUrl\":\"/openapi/acme%20corp/dynamic.json\"", html);
        Assert.Contains("\"tenantSwaggerUrl\":\"https://docs.example.com/acme%20corp/swagger\"", html);
        Assert.Contains("src=\"/tenants/acme%20corp/panel/admin.js?v=", html);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/tenants/acme/panel/admin.css")).StatusCode);

        // Placeholders that aren't route values of the panel are left for the tenant picker.
        html = await host.Client.GetStringAsync("/panel/");
        Assert.Contains("\"tenantOpenApiUrl\":\"/openapi/{tenant}/dynamic.json\"", html);
        Assert.Contains("\"tenantSwaggerUrl\":null", html);

        await Assert.ThrowsAsync<DynamicEndpointValidationException>(() => host.Manager.CreateAsync(DynamicEndpoint.Get("/tenants/x").HandledBy("echo")));
    }

    [Fact]
    public async Task The_admin_api_reports_what_it_supports()
    {
        await using (var plain = await TestHost.StartAsync())
        {
            var info = await plain.Client.GetFromJsonAsync<JsonObject>("/admin/endpoints/info");
            Assert.Null(info!["tenancy"]);
            Assert.Null(info["tenant"]);
            Assert.True(info["revisions"]!.GetValue<bool>()); // the in-memory store keeps history
            Assert.False(info["auditLog"]!.GetValue<bool>());
            Assert.Equal("[\"json\"]", info["formats"]!.ToJsonString());
        }

        await using var host = await TestHost.StartAsync(
            configure: b => b.UseMultiTenancy(t => t.FromHeader("X-Tenant").FromRoutePrefix("/t/{tenant}")).AddAuditLog(a => a.ToMemory()),
            configureApp: app => app.MapDynamicEndpointsTenantAdmin("/admin/tenants/{tenant}/endpoints"));

        var global = await host.Client.GetFromJsonAsync<JsonObject>("/admin/endpoints/info");
        Assert.Equal("""{"routePrefix":"/t/{tenant}","routeParameter":"tenant","header":"X-Tenant"}""", global!["tenancy"]!.ToJsonString());
        Assert.Null(global["tenant"]);
        Assert.True(global["auditLog"]!.GetValue<bool>());

        var scoped = await host.Client.GetFromJsonAsync<JsonObject>("/admin/tenants/acme/endpoints/info");
        Assert.Equal("acme", scoped!["tenant"]!.GetValue<string>());
    }

    /// <summary>Authenticates whoever sends an <c>X-User</c> header.</summary>
    private sealed class HeaderAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(Request.Headers["X-User"] is [{ } user]
                ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, user)], Scheme.Name)), Scheme.Name))
                : AuthenticateResult.NoResult());
    }
}
