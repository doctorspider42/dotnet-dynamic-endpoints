using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicEndpoints.Samples.Tests;

/// <summary>
/// One sample app on TestServer, with a SQLite file of its own in the temp directory (instead of the one in the working
/// directory), deleted afterwards. <paramref name="settings"/> override more configuration values.
/// </summary>
public sealed class SampleApp<TProgram>(params (string Key, string? Value)[] settings) : WebApplicationFactory<TProgram>
    where TProgram : class
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"dynamic-endpoints-sample-{Guid.NewGuid():N}.db");

    /// <summary>Service replacements for the test, applied after the app's own registrations.</summary>
    public Action<IServiceCollection>? TestServices { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", $"Data Source={_databasePath};Pooling=False");
        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }

        if (ProcessorHttpToTestServer)
        {
            builder.ConfigureTestServices(services =>
                services.AddHttpClient("DynamicEndpoints").ConfigurePrimaryHttpMessageHandler(() => Server.CreateHandler()));
        }

        if (TestServices is not null)
        {
            builder.ConfigureTestServices(TestServices);
        }
    }

    /// <summary>
    /// Sends the built-in HTTP processors' requests (the named client "DynamicEndpoints") to this TestServer – for samples whose
    /// forwards and webhooks target the app itself. Set <c>Upstream:BaseUrl</c> to <c>http://localhost</c> with it.
    /// </summary>
    public bool ProcessorHttpToTestServer { get; init; }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // Still open on a slow machine – it's in the temp directory.
            }
        }
    }
}
