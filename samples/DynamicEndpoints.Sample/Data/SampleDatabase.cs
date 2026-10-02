using DynamicEndpoints.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DynamicEndpoints.Sample.Data;

/// <summary>Demo only – a real application uses migrations (<c>dotnet ef migrations add …</c>).</summary>
internal static class SampleDatabase
{
    public static async Task EnsureCreatedAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<AppDbContext>>();

        // Several replicas (e.g. under Aspire) may race to create the schema – the loser simply tries again.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.Database.EnsureCreatedAsync();
                break;
            }
            catch (Exception ex) when (attempt < 5)
            {
                logger.LogDebug(ex, "Creating the sample database failed, retrying.");
                await Task.Delay(TimeSpan.FromSeconds(attempt));
            }
        }

        // EnsureCreated doesn't add tables to an existing database. A local SQLite demo database from before history and
        // drafts existed is simply recreated (and seeded again).
        if (!db.Database.IsSqlite())
        {
            return;
        }

        try
        {
            await db.Set<DynamicEndpointDraftRecord>().AnyAsync();
        }
        catch (Exception)
        {
            logger.LogWarning("The sample database is outdated – recreating it.");
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
        }
    }
}
