using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Coflnet.Payments.Services;

/// <summary>
/// Makes sure every 13 or 52 week Stripe subscription continues every 4 weeks after its first term
/// (<see cref="SubscriptionService.ConvertDueRenewals"/>): converts the subscriptions sold before the 4-week renewal existed
/// and retries conversions a webhook could not complete. Runs every SUBSCRIPTIONS:RENEWAL_CONVERSION_INTERVAL_MINUTES
/// (default 60, plus a random delay of up to a quarter of it); SUBSCRIPTIONS:RENEWAL_CONVERSION_ENABLED=false turns it off.
/// </summary>
public class SubscriptionRenewalConversionService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IConfiguration config;
    private readonly ILogger<SubscriptionRenewalConversionService> logger;

    public SubscriptionRenewalConversionService(IServiceScopeFactory scopeFactory, IConfiguration config,
        ILogger<SubscriptionRenewalConversionService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.config = config;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.Equals(config["SUBSCRIPTIONS:RENEWAL_CONVERSION_ENABLED"], "false", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("The 4-week renewal conversion of 13 and 52 week subscriptions is disabled");
            return;
        }
        var minutes = int.TryParse(config["SUBSCRIPTIONS:RENEWAL_CONVERSION_INTERVAL_MINUTES"], out var m) && m > 0 ? m : 60;
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2) + Jitter(minutes), stoppingToken); // after the startup migration
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunOnce(stoppingToken);
                await Task.Delay(TimeSpan.FromMinutes(minutes) + Jitter(minutes), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Up to a quarter of the interval, so the replicas do not run in step (a subscription another replica is converting is
    /// skipped, see <see cref="SubscriptionBusyException"/>)
    /// </summary>
    internal static TimeSpan Jitter(int intervalMinutes) =>
        TimeSpan.FromSeconds(Random.Shared.NextDouble() * intervalMinutes * 60 / 4);

    private async Task RunOnce(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<SubscriptionService>().ConvertDueRenewals(stoppingToken);
        }
        catch (Exception e) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError(e, "4-week renewal conversion run failed");
        }
    }
}
