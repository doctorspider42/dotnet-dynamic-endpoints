using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace DynamicEndpoints.Samples.Tests;

/// <summary>
/// One sample app on TestServer, with a SQLite file of its own in the temp directory (instead of the one in the working
/// directory), deleted afterwards. <paramref name="settings"/> override more configuration values.
/// </summary>
public sealed class SampleApp<TProgram>(params (string Key, string? Value)[] settings) : WebApplicationFactory<TProgram>
    where TProgram : class
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"dynamic-endpoints-sample-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", $"Data Source={_databasePath};Pooling=False");
        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }
    }

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
