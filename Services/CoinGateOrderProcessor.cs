using System;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Models.CoinGate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Coflnet.Payments.Services;

/// <summary>
/// The facts of a paid CoinGate order needed to credit it and record the revenue.
/// Built from either a webhook callback or an API order.
/// </summary>
public class CoinGateOrder
{
    public long Id { get; set; }
    public string OrderId { get; set; }
    public decimal PriceAmount { get; set; }
    public string PriceCurrency { get; set; }
    public decimal? ReceiveAmount { get; set; }
    public string PayCurrency { get; set; }
    /// <summary>
    /// Time recorded as payment time (callback: creation time, reconciliation: paid_at)
    /// </summary>
    public DateTime Timestamp { get; set; }

    public static CoinGateOrder FromCallback(CoinGateCallback callback) => new()
    {
        Id = callback.Id,
        OrderId = callback.OrderId,
        PriceAmount = callback.PriceAmount,
        PriceCurrency = callback.PriceCurrency,
        ReceiveAmount = callback.ReceiveAmount,
        PayCurrency = callback.PayCurrency,
        Timestamp = callback.CreatedAt
    };

    public static CoinGateOrder FromResponse(CoinGateOrderResponse order) => new()
    {
        Id = order.Id,
        OrderId = order.OrderId,
        PriceAmount = order.PriceAmount,
        PriceCurrency = order.PriceCurrency,
        ReceiveAmount = order.ReceiveAmount,
        PayCurrency = order.PayCurrency,
        Timestamp = order.PaidAt ?? order.CreatedAt
    };
}

/// <summary>
/// Credits a paid CoinGate order and records it for tax reporting.
/// Shared by the webhook callback and the reconciliation service.
/// </summary>
public class CoinGateOrderProcessor
{
    private readonly PaymentContext db;
    private readonly TransactionService transactionService;
    private readonly IPaymentEventProducer paymentEventProducer;
    private readonly ILogger<CoinGateOrderProcessor> logger;

    public CoinGateOrderProcessor(
        PaymentContext db,
        TransactionService transactionService,
        IPaymentEventProducer paymentEventProducer,
        ILogger<CoinGateOrderProcessor> logger)
    {
        this.db = db;
        this.transactionService = transactionService;
        this.paymentEventProducer = paymentEventProducer;
        this.logger = logger;
    }

    /// <summary>
    /// Credits the order once (reference coingate:{id}) and writes the PaymentRecord.
    /// </summary>
    /// <returns>true if newly processed, false if it was already recorded</returns>
    public async Task<bool> ProcessPaidOrderAsync(CoinGateOrder order, string userId, string productId, int coinAmount, string source, string ipAddress = null)
    {
        var productIdNumber = int.Parse(productId);
        var user = await db.Users.Where(u => u.ExternalId == userId).FirstOrDefaultAsync();
        try
        {
            // Use the CoinGate order ID as the transaction reference
            var reference = $"coingate:{order.Id}";
            await transactionService.AddTopUp(productIdNumber, userId, reference, coinAmount);

            await paymentEventProducer.ProduceEvent(new PaymentEvent
            {
                PayedAmount = (double)order.PriceAmount,
                ProductId = productId,
                UserId = userId,
                Currency = order.PriceCurrency,
                PaymentMethod = order.PayCurrency ?? "crypto",
                PaymentProvider = "coingate",
                PaymentProviderTransactionId = order.Id.ToString(),
                Timestamp = order.Timestamp
            });

            logger.LogInformation("CoinGate topup processed ({Source}): {OrderId}, {Amount} coins for user {UserId}",
                source, order.OrderId, coinAmount, userId);

            // Record for tax compliance - CoinGate does NOT remit tax
            await RecordPayment(new PaymentRecord
            {
                UserId = user?.Id ?? 0,
                ExternalUserId = userId,
                Country = user?.Country,
                ZipCode = user?.Zip,
                GrossAmount = order.PriceAmount,
                Subtotal = order.PriceAmount,
                TaxAmount = 0,
                TaxRemittedByProcessor = false,
                NetAmount = order.ReceiveAmount ?? order.PriceAmount,
                ProcessorFee = order.PriceAmount - (order.ReceiveAmount ?? order.PriceAmount),
                Currency = order.PriceCurrency?.ToUpper() ?? "USD",
                Provider = "coingate",
                PaymentMethod = order.PayCurrency ?? "crypto",
                ExternalOrderId = order.OrderId,
                ExternalTransactionId = order.Id.ToString(),
                ProductSlug = productId,
                ProductId = productIdNumber,
                CoinAmount = coinAmount,
                PaidAt = order.Timestamp,
                Status = PaymentRecordStatus.Confirmed,
                IpAddress = ipAddress
            });
            return true;
        }
        catch (TransactionService.DupplicateTransactionException)
        {
            logger.LogInformation("CoinGate duplicate transaction for order {OrderId} ({Source})", order.OrderId, source);
            return false;
        }
    }

    private async Task RecordPayment(PaymentRecord record)
    {
        try
        {
            record.RecordedAt = DateTime.UtcNow;
            if (record.PaidAt == default)
                record.PaidAt = DateTime.UtcNow;
            // PostgreSQL timestamp with time zone requires UTC
            if (record.PaidAt.Kind != DateTimeKind.Utc)
                record.PaidAt = record.PaidAt.ToUniversalTime();
            if (record.NetAmount == 0 && record.GrossAmount > 0)
                record.NetAmount = record.GrossAmount - record.TaxAmount - record.ProcessorFee;

            db.PaymentRecords.Add(record);
            await db.SaveChangesAsync();
            logger.LogInformation("PaymentRecord created: {Provider} {ExternalOrderId} {GrossAmount} {Currency} for user {UserId}",
                record.Provider, record.ExternalOrderId, record.GrossAmount, record.Currency, record.ExternalUserId);
        }
        catch (Exception ex)
        {
            // Never let a recording failure break the actual payment flow
            logger.LogError(ex, "Failed to record PaymentRecord for {Provider} order {ExternalOrderId}", record.Provider, record.ExternalOrderId);
        }
    }
}
