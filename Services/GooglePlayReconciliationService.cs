using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services.Subscriptions;
using Google;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Coflnet.Payments.Services;

/// <summary>Reconciles live Play subscriptions whose last sync is several hours old.</summary>
public class GooglePlayReconciliationService(
    IServiceScopeFactory scopeFactory, ILogger<GooglePlayReconciliationService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(3) + SubscriptionRenewalConversionService.Jitter(30), stoppingToken);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                await RunOnce(stoppingToken);
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunOnce(CancellationToken stoppingToken)
    {
        // Leave a little scheduling headroom. Recheck the cutoff under the row lease to dedupe replicas.
        var cutoff = DateTime.UtcNow - Interval + TimeSpan.FromMinutes(15);
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
            var ids = await db.Subscriptions.Where(s => s.ProviderSlug == SubscriptionProviders.GooglePlay && s.Status != "expired"
                    && s.ProviderToken != null && s.UpdatedAt <= cutoff)
                .Select(s => s.ExternalId).ToListAsync(stoppingToken);
            foreach (var id in ids)
            {
                stoppingToken.ThrowIfCancellationRequested();
                await Reconcile(id, cutoff);
            }
        }
        catch (Exception e) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError("Google Play reconciliation run failed ({ExceptionType})", e.GetType().Name);
        }
    }

    private async Task Reconcile(string id, DateTime cutoff)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<SubscriptionService>().ReconcileGooglePlay(id, cutoff);
        }
        catch (SubscriptionBusyException)
        {
            // A webhook or another replica already has this subscription.
        }
        catch (Exception e)
        {
            logger.LogError("Google Play reconciliation failed for {SubscriptionId} ({ExceptionType}, HTTP {StatusCode})",
                id, e.GetType().Name, (int?)(e as GoogleApiException)?.HttpStatusCode);
        }
    }
}
