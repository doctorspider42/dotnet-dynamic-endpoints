using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicEndpoints.Testing;

public sealed class DynamicEndpointsTestServerOptions
{
    /// <summary>Library options, e.g. limits or <see cref="DynamicEndpointsOptions.DefaultProcessor"/>.</summary>
    public Action<DynamicEndpointsOptions>? Options { get; set; }

    /// <summary>Processors, validators, filters, … of the code under test.</summary>
    public Action<IDynamicEndpointsBuilder>? DynamicEndpoints { get; set; }

    /// <summary>Further services (authentication, fakes of your dependencies, …).</summary>
    public Action<IServiceCollection>? Services { get; set; }

    /// <summary>Middleware and endpoints, added before <c>MapDynamicEndpoints()</c>.</summary>
    public Action<WebApplication>? Configure { get; set; }

    /// <summary>Store to use – share one between servers to simulate several instances.</summary>
    public InMemoryDynamicEndpointStore? Store { get; set; }

    /// <summary>Maps the admin API under this prefix; <c>null</c> (default) leaves it out.</summary>
    public string? AdminPrefix { get; set; }

    /// <summary>Maps the OpenAPI document of the dynamic endpoints here. Default <c>/openapi/dynamic-endpoints.json</c>.</summary>
    public string? OpenApiPath { get; set; } = "/openapi/dynamic-endpoints.json";
}

/// <summary>
/// A minimal in-memory application serving dynamic endpoints – test processors and validators end to end without your
/// application or a database.
/// </summary>
/// <example>
/// <code>
/// await using var server = await DynamicEndpointsTestServer.StartAsync(b =&gt; b.AddProcessor&lt;OrderLookupProcessor&gt;());
/// await server.AddEndpointAsync(DynamicEndpoint.Get("/orders/{id}").HandledBy("order-lookup").FromRoute("id"));
/// var response = await server.Client.GetAsync("/orders/42");
/// </code>
/// </example>
public sealed class DynamicEndpointsTestServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private DynamicEndpointsTestServer(WebApplication app)
    {
        _app = app;
        Client = app.GetTestClient();
    }

    public HttpClient Client { get; }

    public IServiceProvider Services => _app.Services;

    public IDynamicEndpointManager Manager => _app.Services.GetRequiredService<IDynamicEndpointManager>();

    public IDynamicOpenApiDocumentProvider OpenApi => _app.Services.GetRequiredService<IDynamicOpenApiDocumentProvider>();

    public static Task<DynamicEndpointsTestServer> StartAsync(Action<IDynamicEndpointsBuilder> configure) =>
        StartAsync(new DynamicEndpointsTestServerOptions { DynamicEndpoints = configure });

    public static async Task<DynamicEndpointsTestServer> StartAsync(DynamicEndpointsTestServerOptions? options = null)
    {
        options ??= new DynamicEndpointsTestServerOptions();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthorization();

        // In-memory first, so the configuration below can still add e.g. a change notifier shared by several servers.
        var dynamicEndpoints = builder.Services.AddDynamicEndpoints(options.Options);
        builder.Services.UseInMemoryDynamicEndpoints(options.Store);
        options.DynamicEndpoints?.Invoke(dynamicEndpoints);
        options.Services?.Invoke(builder.Services);

        var app = builder.Build();
        options.Configure?.Invoke(app);
        app.MapDynamicEndpoints();
        if (options.AdminPrefix is not null)
        {
            app.MapDynamicEndpointsAdmin(options.AdminPrefix);
        }

        if (options.OpenApiPath is not null)
        {
            app.MapDynamicEndpointsOpenApi(options.OpenApiPath);
        }

        await app.StartAsync();
        return new DynamicEndpointsTestServer(app);
    }

    /// <summary>Creates (or replaces) an endpoint and returns the stored definition.</summary>
    public Task<DynamicEndpointDefinition> AddEndpointAsync(DynamicEndpointDefinition definition, CancellationToken cancellationToken = default) =>
        Manager.UpsertAsync(definition, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
