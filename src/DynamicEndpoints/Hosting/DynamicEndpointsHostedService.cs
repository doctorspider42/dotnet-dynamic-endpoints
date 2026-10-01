using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DynamicEndpoints.Hosting;

/// <summary>Loads persisted definitions on start-up and (optionally) keeps polling the store.</summary>
internal sealed class DynamicEndpointsHostedService(
    IDynamicEndpointManager manager,
    IServiceScopeFactory scopeFactory,
    IOptions<DynamicEndpointsOptions> options,
    ILogger<DynamicEndpointsHostedService> logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await manager.ReloadAsync(cancellationToken);

            await using var scope = scopeFactory.CreateAsyncScope();
            foreach (var seeder in scope.ServiceProvider.GetServices<IDynamicEndpointSeeder>())
            {
                logger.LogDebug("Running dynamic endpoint seeder {Seeder}.", seeder.GetType().Name);
                await seeder.SeedAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (!options.Value.ThrowOnStartupLoadFailure && ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Dynamic endpoints could not be loaded on start-up.");
        }

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.RefreshInterval is not { } interval || interval <= TimeSpan.Zero)
        {
            return;
        }

        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await manager.ReloadAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Refreshing dynamic endpoints failed; keeping the current set.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
