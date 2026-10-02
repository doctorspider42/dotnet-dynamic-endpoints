using System.Threading.RateLimiting;
using DynamicEndpoints.Sample.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DynamicEndpoints.Sample.Demo;

/// <summary>Settings of a public live demo (section <c>Demo</c>, e.g. <c>Demo__Enabled=true</c> in the container).</summary>
public sealed class DemoOptions
{
    public bool Enabled { get; set; }

    /// <summary>Everybody edits the same endpoints – they go back to the seeded ones this often.</summary>
    public TimeSpan ResetInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Requests per client IP and minute, for all endpoints together.</summary>
    public int RequestsPerMinute { get; set; } = 120;
}

internal static class DemoMode
{
    /// <summary>A rate limit per client and a periodic reset – the admin API stays open, it's a demo.</summary>
    public static DemoOptions AddDemoMode(this WebApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection("Demo");
        builder.Services.Configure<DemoOptions>(section);
        var demo = section.Get<DemoOptions>() ?? new DemoOptions();
        if (!demo.Enabled)
        {
            return demo;
        }

        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = demo.RequestsPerMinute, Window = TimeSpan.FromMinutes(1) }));
        });
        builder.Services.AddHostedService<DemoResetService>();
        return demo;
    }
}

/// <summary>Deletes every endpoint (with its history and drafts) and every note, then seeds the demo endpoints again.</summary>
internal sealed class DemoResetService(IServiceScopeFactory scopeFactory, IOptions<DemoOptions> options, ILogger<DemoResetService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.ResetInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ResetAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Resetting the demo failed.");
            }
        }
    }

    private async Task ResetAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IDynamicEndpointManager>();
        foreach (var state in await manager.ListAsync(cancellationToken))
        {
            await manager.DeleteAsync(state.Definition.Id, cancellationToken);
        }

        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Documents.ExecuteDeleteAsync(cancellationToken);
        foreach (var seeder in scope.ServiceProvider.GetServices<IDynamicEndpointSeeder>())
        {
            await seeder.SeedAsync(cancellationToken);
        }

        logger.LogInformation("The demo was reset.");
    }
}
