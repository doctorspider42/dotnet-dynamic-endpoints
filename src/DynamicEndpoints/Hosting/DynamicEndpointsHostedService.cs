using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DynamicEndpoints.Hosting;

/// <summary>
/// Loads persisted definitions on start-up and keeps them current: polling (<see cref="DynamicEndpointsOptions.RefreshInterval"/>)
/// and change notifications both request reloads, which are coalesced – a burst of notifications costs one reload. Also publishes
/// scheduled drafts (<see cref="DynamicEndpointsOptions.ScheduledPublishInterval"/>).
/// </summary>
internal sealed class DynamicEndpointsHostedService(
    IDynamicEndpointManager manager,
    IServiceScopeFactory scopeFactory,
    IOptions<DynamicEndpointsOptions> options,
    ILogger<DynamicEndpointsHostedService> logger,
    IDynamicEndpointChangeNotifier? notifier = null) : BackgroundService, IDynamicEndpointChangeListener
{
    private static readonly TimeSpan ListenRetryDelay = TimeSpan.FromSeconds(5);

    // One pending request is enough: everything requested while a reload runs is covered by the next one.
    private readonly Channel<bool> _reloads = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
    });

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                foreach (var initializer in scope.ServiceProvider.GetServices<IDynamicEndpointStoreInitializer>())
                {
                    logger.LogDebug("Initializing dynamic endpoint store with {Initializer}.", initializer.GetType().Name);
                    await initializer.InitializeAsync(cancellationToken);
                }
            }

            await manager.ReloadAsync(cancellationToken);

            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                foreach (var seeder in scope.ServiceProvider.GetServices<IDynamicEndpointSeeder>())
                {
                    logger.LogDebug("Running dynamic endpoint seeder {Seeder}.", seeder.GetType().Name);
                    await seeder.SeedAsync(cancellationToken);
                }
            }
        }
        catch (Exception ex) when (!options.Value.ThrowOnStartupLoadFailure && ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Dynamic endpoints could not be loaded on start-up.");
        }

        await base.StartAsync(cancellationToken);
    }

    public Task OnConnectedAsync(CancellationToken cancellationToken)
    {
        logger.LogDebug("Listening for dynamic endpoint change notifications.");
        RequestReload();
        return Task.CompletedTask;
    }

    public Task OnNotificationAsync(DynamicEndpointChangeNotification notification, CancellationToken cancellationToken)
    {
        if (notification.InstanceId != options.Value.InstanceId)
        {
            logger.LogDebug("Instance {Instance} changed dynamic endpoints {Ids}.", notification.InstanceId, notification.EndpointIds);
            RequestReload();
        }

        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.WhenAll(ReloadLoopAsync(stoppingToken), PollAsync(stoppingToken), ListenAsync(stoppingToken), PublishScheduledAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private void RequestReload() => _reloads.Writer.TryWrite(true);

    private async Task ReloadLoopAsync(CancellationToken stoppingToken)
    {
        await foreach (var _ in _reloads.Reader.ReadAllAsync(stoppingToken))
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

    private async Task PollAsync(CancellationToken stoppingToken)
    {
        if (options.Value.RefreshInterval is not { } interval || interval <= TimeSpan.Zero)
        {
            return;
        }

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            RequestReload();
        }
    }

    private async Task PublishScheduledAsync(CancellationToken stoppingToken)
    {
        if (options.Value.ScheduledPublishInterval is not { } interval || interval <= TimeSpan.Zero)
        {
            return;
        }

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await manager.PublishDueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Publishing scheduled dynamic endpoint drafts failed; retrying in {Interval}.", interval);
            }
        }
    }

    private async Task ListenAsync(CancellationToken stoppingToken)
    {
        if (notifier is null)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await notifier.ListenAsync(this, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Listening for dynamic endpoint change notifications failed; retrying in {Delay}.", ListenRetryDelay);
                await Task.Delay(ListenRetryDelay, stoppingToken);
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                // ListenAsync returned without being cancelled – don't spin.
                await Task.Delay(ListenRetryDelay, stoppingToken);
            }
        }
    }
}
