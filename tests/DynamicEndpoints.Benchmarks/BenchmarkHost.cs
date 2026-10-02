using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicEndpoints.Benchmarks;

/// <summary>In-process application with dynamic endpoints (in-memory store) and logging off.</summary>
internal sealed class BenchmarkHost(WebApplication app) : IAsyncDisposable
{
    public HttpClient Client { get; } = app.GetTestClient();

    public IServiceProvider Services => app.Services;

    public IDynamicEndpointManager Manager => app.Services.GetRequiredService<IDynamicEndpointManager>();

    public static async Task<BenchmarkHost> StartAsync(Action<IDynamicEndpointsBuilder> configure, Action<WebApplication>? configureApp = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        configure(builder.Services.AddDynamicEndpoints());

        var app = builder.Build();
        configureApp?.Invoke(app);
        app.MapDynamicEndpoints();
        await app.StartAsync();
        return new BenchmarkHost(app);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
    }
}

internal static class HttpResponseMessageExtensions
{
    /// <summary>Reads the body (so the full response is part of the measurement) and checks the status.</summary>
    public static async Task<int> ReadAsync(this HttpResponseMessage response, int expectedStatus)
    {
        using (response)
        {
            var body = await response.Content.ReadAsByteArrayAsync();
            if ((int)response.StatusCode != expectedStatus)
            {
                throw new InvalidOperationException(
                    $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: expected {expectedStatus}, got {(int)response.StatusCode}: {System.Text.Encoding.UTF8.GetString(body)}");
            }

            return body.Length;
        }
    }
}
