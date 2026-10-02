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

    // Databases set up with EnsureCreated have the tables but no migration history – record the migrations whose tables exist as
    // applied, instead of failing on "table already exists".
    private async Task AdoptEnsureCreatedTableAsync(CancellationToken cancellationToken)
    {
        var applied = (await context.Database.GetAppliedMigrationsAsync(cancellationToken)).ToHashSet();
        (string Id, Func<Task<bool>> TablesExist)[] migrations =
        [
            (InitialDynamicEndpoints.Id, () => TableExistsAsync<DynamicEndpointRecord>(cancellationToken)),
            (DynamicEndpointRevisionsAndDrafts.Id, async () =>
                await TableExistsAsync<DynamicEndpointRevisionRecord>(cancellationToken) &&
                await TableExistsAsync<DynamicEndpointDraftRecord>(cancellationToken)),
        ];

        IHistoryRepository? history = null;
        foreach (var (id, tablesExist) in migrations)
        {
            if (applied.Contains(id))
            {
                continue;
            }

            if (!await tablesExist())
            {
                return;
            }

            if (history is null)
            {
                logger.LogInformation("Adopting existing dynamic endpoint tables (created without migrations) into the migration history.");
                history = context.GetService<IHistoryRepository>();
                await history.CreateIfNotExistsAsync(cancellationToken);
            }

            var insert = history.GetInsertScript(new HistoryRow(id, ProductInfo.GetVersion()));
            await context.Database.ExecuteSqlRawAsync(insert, cancellationToken);
        }
    }

    private async Task<bool> TableExistsAsync<TRecord>(CancellationToken cancellationToken)
        where TRecord : class
    {
        try
        {
            await context.Set<TRecord>().AsNoTracking().AnyAsync(cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }
}
