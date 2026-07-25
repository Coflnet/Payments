using System;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Prometheus;

namespace Coflnet.Payments.Services;

public static class PaymentMetrics
{
    public static readonly Counter StripeCredentialErrors = Metrics.CreateCounter(
        "payments_stripe_credential_errors_total",
        "Stripe API requests rejected because the API credentials are invalid or lack permission");
}

public class PaymentMetricService : BackgroundService
{
    private static readonly Gauge PendingRefunds = Metrics.CreateGauge(
        "payments_manual_refunds_pending",
        "Stripe payments captured but awaiting a manual refund");
    private static readonly Gauge PurchasesLastHour = Metrics.CreateGauge(
        "payments_purchases_last_hour",
        "Confirmed payment records with a payment timestamp in the last hour");
    private static readonly Gauge PurchasesLast24Hours = Metrics.CreateGauge(
        "payments_purchases_last_24_hours",
        "Confirmed payment records with a payment timestamp in the last 24 hours");
    private static readonly Gauge OpenRequests = Metrics.CreateGauge(
        "payments_open_requests",
        "Payment requests currently waiting for completion");

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PaymentMetricService> _logger;

    public PaymentMetricService(
        IServiceScopeFactory scopeFactory,
        ILogger<PaymentMetricService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
                var now = DateTime.UtcNow;

                PendingRefunds.Set(await db.PaymentRequests.CountAsync(
                    request => request.State == PaymentRequest.Status.REFUND_PENDING,
                    stoppingToken));
                OpenRequests.Set(await db.PaymentRequests.CountAsync(
                    request => request.State == PaymentRequest.Status.CREATED
                        || request.State == PaymentRequest.Status.CONFIRMED
                        || request.State == PaymentRequest.Status.WAITING
                        || request.State == PaymentRequest.Status.PROCESSING,
                    stoppingToken));
                PurchasesLastHour.Set(await db.PaymentRecords.CountAsync(
                    payment => payment.Status == PaymentRecordStatus.Confirmed
                        && payment.PaidAt >= now.AddHours(-1),
                    stoppingToken));
                PurchasesLast24Hours.Set(await db.PaymentRecords.CountAsync(
                    payment => payment.Status == PaymentRecordStatus.Confirmed
                        && payment.PaidAt >= now.AddHours(-24),
                    stoppingToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to refresh payment business metrics");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
