using System.Net;
using System.Net.Http.Headers;
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
        Assert.Contains("function openHistory", await script.Content.ReadAsStringAsync());

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
