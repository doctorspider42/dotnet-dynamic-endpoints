using DynamicEndpoints.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;

namespace DynamicEndpoints.EntityFrameworkCore;

internal sealed class MigrateDatabaseInitializer<TContext>(TContext context, ILogger<MigrateDatabaseInitializer<TContext>> logger)
    : IDynamicEndpointStoreInitializer
    where TContext : DbContext
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (context is DynamicEndpointsDbContext)
        {
            await AdoptEnsureCreatedTableAsync(cancellationToken);
        }

        await context.Database.MigrateAsync(cancellationToken);
    }

    // Databases set up with EnsureCreated have the table but no migration history – record the initial migration as applied,
    // instead of failing on "table already exists".
    private async Task AdoptEnsureCreatedTableAsync(CancellationToken cancellationToken)
    {
        var applied = await context.Database.GetAppliedMigrationsAsync(cancellationToken);
        if (applied.Contains(InitialDynamicEndpoints.Id) || !await TableExistsAsync(cancellationToken))
        {
            return;
        }

        logger.LogInformation("Adopting the existing dynamic endpoints table (created without migrations) into the migration history.");
        var history = context.GetService<IHistoryRepository>();
        await history.CreateIfNotExistsAsync(cancellationToken);
        var insert = history.GetInsertScript(new HistoryRow(InitialDynamicEndpoints.Id, ProductInfo.GetVersion()));
        await context.Database.ExecuteSqlRawAsync(insert, cancellationToken);
    }

    private async Task<bool> TableExistsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await context.Set<DynamicEndpointRecord>().AsNoTracking().AnyAsync(cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }
}
