using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Coflnet.Payments.Services;

/// <summary>
/// Publishes the renewal reminders of 13 and 52 week subscriptions (<see cref="SubscriptionService.SendRenewalReminders"/>).
/// Runs every SUBSCRIPTIONS:RENEWAL_REMINDER_INTERVAL_MINUTES (default 60, plus a random delay of up to a quarter of it);
/// SUBSCRIPTIONS:RENEWAL_REMINDERS_ENABLED=false turns it off. Each publish waits at most
/// SUBSCRIPTIONS:RENEWAL_REMINDER_PUBLISH_TIMEOUT_SECONDS (default 30), so an unreachable Kafka does not hang the loop; at most
/// SUBSCRIPTIONS:RENEWAL_REMINDERS_PER_RUN (default 50) are published per run.
/// </summary>
public class SubscriptionRenewalReminderService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IConfiguration config;
    private readonly ILogger<SubscriptionRenewalReminderService> logger;

    public SubscriptionRenewalReminderService(IServiceScopeFactory scopeFactory, IConfiguration config,
        ILogger<SubscriptionRenewalReminderService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.config = config;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.Equals(config["SUBSCRIPTIONS:RENEWAL_REMINDERS_ENABLED"], "false", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Renewal reminders of 13 and 52 week subscriptions are disabled");
            return;
        }
        var minutes = PositiveSetting("SUBSCRIPTIONS:RENEWAL_REMINDER_INTERVAL_MINUTES", 60);
        try
        {
            // after the startup migration; the jitter keeps the replicas apart (a concurrent duplicate is deduped by the consumer)
            await Task.Delay(TimeSpan.FromMinutes(3) + SubscriptionRenewalConversionService.Jitter(minutes), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunOnce(stoppingToken);
                await Task.Delay(TimeSpan.FromMinutes(minutes) + SubscriptionRenewalConversionService.Jitter(minutes), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private int PositiveSetting(string key, int fallback) => int.TryParse(config[key], out var value) && value > 0 ? value : fallback;

    private async Task RunOnce(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var producer = scope.ServiceProvider.GetRequiredService<IPaymentEventProducer>();
            var timeout = TimeSpan.FromSeconds(PositiveSetting("SUBSCRIPTIONS:RENEWAL_REMINDER_PUBLISH_TIMEOUT_SECONDS", 30));
            var perRun = PositiveSetting("SUBSCRIPTIONS:RENEWAL_REMINDERS_PER_RUN", SubscriptionService.DefaultRenewalRemindersPerRun);
            var sent = await scope.ServiceProvider.GetRequiredService<SubscriptionService>()
                .SendRenewalReminders(producer, timeout, perRun, stoppingToken);
            if (sent > 0)
                logger.LogInformation("Published {Count} subscription renewal reminders", sent);
        }
        catch (Exception e) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError(e, "Subscription renewal reminder run failed");
        }
    }
}
