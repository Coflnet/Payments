using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Coflnet.Payments.Services;

public sealed class PaymentConfirmationOutboxPublisher : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IPaymentEventProducer producer;
    private readonly ILogger<PaymentConfirmationOutboxPublisher> logger;

    public PaymentConfirmationOutboxPublisher(
        IServiceScopeFactory scopeFactory,
        IPaymentEventProducer producer,
        ILogger<PaymentConfirmationOutboxPublisher> logger)
    {
        this.scopeFactory = scopeFactory;
        this.producer = producer;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await ProcessOneAsync(stoppingToken))
                    await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Payment confirmation outbox iteration failed");
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
    }

    internal async Task<bool> ProcessOneAsync(
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        var candidateId = await db.PaymentConfirmationOutbox
            .AsNoTracking()
            .Where(row => row.PublishedAt == null
                && row.NextAttemptAt <= now
                && (row.LeaseUntil == null || row.LeaseUntil <= now))
            .OrderBy(row => row.CreatedAt)
            .Select(row => (long?)row.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (!candidateId.HasValue)
            return false;

        var leaseId = Guid.NewGuid();
        var claimed = await db.PaymentConfirmationOutbox
            .Where(row => row.Id == candidateId.Value
                && row.PublishedAt == null
                && row.NextAttemptAt <= now
                && (row.LeaseUntil == null || row.LeaseUntil <= now))
            .ExecuteUpdateAsync(update => update
                .SetProperty(row => row.LeaseId, leaseId)
                .SetProperty(row => row.LeaseUntil, now + LeaseDuration)
                .SetProperty(row => row.Attempts, row => row.Attempts + 1),
                cancellationToken);
        if (claimed == 0)
            return true;

        var row = await db.PaymentConfirmationOutbox
            .AsNoTracking()
            .SingleAsync(item => item.Id == candidateId.Value
                && item.LeaseId == leaseId, cancellationToken);
        try
        {
            var payment = JsonConvert.DeserializeObject<PaymentEvent>(row.Payload)
                ?? throw new InvalidOperationException(
                    "Payment confirmation payload is empty");
            await producer.ProduceEvent(payment);
            await db.PaymentConfirmationOutbox
                .Where(item => item.Id == row.Id && item.LeaseId == leaseId)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(item => item.PublishedAt, now)
                    .SetProperty(item => item.LeaseId, (Guid?)null)
                    .SetProperty(item => item.LeaseUntil, (DateTime?)null)
                    .SetProperty(item => item.LastError, (string)null),
                    cancellationToken);
        }
        catch (Exception exception)
        {
            var error = exception.ToString();
            if (error.Length > 2000)
                error = error[..2000];
            var retryAt = now + TimeSpan.FromSeconds(
                Math.Min(300, Math.Pow(2, Math.Min(row.Attempts, 8))));
            await db.PaymentConfirmationOutbox
                .Where(item => item.Id == row.Id && item.LeaseId == leaseId)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(item => item.NextAttemptAt, retryAt)
                    .SetProperty(item => item.LeaseId, (Guid?)null)
                    .SetProperty(item => item.LeaseUntil, (DateTime?)null)
                    .SetProperty(item => item.LastError, error),
                    cancellationToken);
            logger.LogWarning(
                exception,
                "Could not publish payment confirmation {Provider}/{PaymentId}; retry scheduled",
                row.Provider,
                row.ProviderTransactionId);
        }

        return true;
    }
}
