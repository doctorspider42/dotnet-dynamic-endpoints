using DynamicEndpoints.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.ComponentModel.DataAnnotations;

namespace DynamicEndpoints.Tests;

internal sealed class TestHost(WebApplication app) : IAsyncDisposable
{
    public HttpClient Client { get; } = app.GetTestClient();

    public IDynamicEndpointManager Manager => app.Services.GetRequiredService<IDynamicEndpointManager>();

    public IServiceProvider Services => app.Services;

    /// <summary>A handler that sends requests to the in-memory server – for clients other than <see cref="Client"/>.</summary>
    public HttpMessageHandler CreateHandler() => app.GetTestServer().CreateHandler();

    public static async Task<TestHost> StartAsync(
        string? sqlitePath = null,
        Action<DynamicEndpointsOptions>? options = null,
        Action<IDynamicEndpointsBuilder>? configure = null,
        Action<WebApplication>? configureApp = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthorization(o => o.AddPolicy("admins", p => p.RequireRole("admin")));

        var dynamicEndpoints = builder.Services
            .AddDynamicEndpoints(o =>
            {
                o.ReservedPrefixes.Add("/internal");
                options?.Invoke(o);
            })
            .AddProcessor("echo", request => Results.Ok(request.Parameters))
            .AddProcessor<GreetingProcessor>();
        configure?.Invoke(dynamicEndpoints);

        if (sqlitePath is not null)
        {
            dynamicEndpoints.UseEntityFrameworkStore(o => o.UseSqlite($"Data Source={sqlitePath};Pooling=False"));
        }

        var app = builder.Build();
        if (sqlitePath is not null)
        {
            using var scope = app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<DynamicEndpointsDbContext>().Database.EnsureCreatedAsync();
        }

        configureApp?.Invoke(app);
        app.MapGet("/health", () => "ok");
        app.MapDynamicEndpoints();
        app.MapDynamicEndpointsAdmin("/admin/endpoints");
        app.MapDynamicEndpointsOpenApi("/openapi/dynamic.json");

        await app.StartAsync();
        return new TestHost(app);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
    }
}

public sealed class GreetingConfig
{
    [Required]
    public string? Greeting { get; set; }
}

[DynamicProcessor("greeting", Description = "Greets", ConfigurationExample = """{ "greeting": "Hi" }""")]
internal sealed class GreetingProcessor : DynamicEndpointProcessor<GreetingConfig>
{
    protected override Task<IResult> ProcessAsync(DynamicRequest request, GreetingConfig configuration) =>
        Task.FromResult(Results.Text($"{configuration.Greeting}, {request.Get<string>("name")}!"));
}
