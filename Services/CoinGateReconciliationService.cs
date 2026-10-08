using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Prometheus;

namespace Coflnet.Payments.Services;

/// <summary>
/// Safety net for the CoinGate webhook: periodically lists paid orders at CoinGate and
/// records every one that the callback did not (credit + PaymentRecord).
/// </summary>
public class CoinGateReconciliationService : BackgroundService
{
    public static readonly Counter Reconciled = Metrics.CreateCounter(
        "payments_coingate_reconciled_total",
        "Paid CoinGate orders that the callback missed and reconciliation recorded");
    public static readonly Counter Unreconcilable = Metrics.CreateCounter(
        "payments_coingate_unreconcilable_total",
        "Paid CoinGate orders that reconciliation could not safely record (price mismatch)");

    private static readonly Regex OrderIdFormat = new(@"^CG-(?<user>.+)-(?<product>\d+)-(?<ticks>\d+)$", RegexOptions.Compiled);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CoinGateService _coinGate;
    private readonly IConfiguration _config;
    private readonly ILogger<CoinGateReconciliationService> _logger;

    public CoinGateReconciliationService(
        IServiceScopeFactory scopeFactory,
        CoinGateService coinGate,
        IConfiguration config,
        ILogger<CoinGateReconciliationService> logger)
    {
        _scopeFactory = scopeFactory;
        _coinGate = coinGate;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrEmpty(_config["COINGATE:API_TOKEN"])
            || string.Equals(_config["COINGATE:RECONCILE_ENABLED"], "false", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("CoinGate reconciliation disabled");
            return;
        }
        var minutes = int.TryParse(_config["COINGATE:RECONCILE_INTERVAL_MINUTES"], out var m) && m > 0 ? m : 60;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    await ReconcileAsync(
                        scope.ServiceProvider.GetRequiredService<PaymentContext>(),
                        scope.ServiceProvider.GetRequiredService<CoinGateOrderProcessor>(),
                        stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "CoinGate reconciliation run failed");
                }
                await Task.Delay(TimeSpan.FromMinutes(minutes), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// One reconciliation run. Returns the number of orders newly recorded.
    /// </summary>
    public async Task<int> ReconcileAsync(PaymentContext db, CoinGateOrderProcessor processor, CancellationToken token = default)
    {
        var lookbackDays = int.TryParse(_config["COINGATE:RECONCILE_LOOKBACK_DAYS"], out var d) && d > 0 ? d : 30;
        var receiveCurrency = _config["COINGATE:RECEIVE_CURRENCY"] ?? "EUR";
        var orders = await _coinGate.ListPaidOrdersAsync(DateTime.UtcNow.AddDays(-lookbackDays));

        var references = orders.Select(o => $"coingate:{o.Id}").Distinct().ToList();
        var existing = new HashSet<string>();
        foreach (var chunk in references.Chunk(500))
        {
            var found = await db.FiniteTransactions
                .Where(t => chunk.Contains(t.Reference))
                .Select(t => t.Reference)
                .ToListAsync(token);
            existing.UnionWith(found);
        }

        var recorded = 0;
        foreach (var order in orders)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (order.Status != Coflnet.Payments.Models.CoinGate.CoinGateOrderStatus.Paid)
                    continue;
                if (existing.Contains($"coingate:{order.Id}"))
                    continue;

                var match = OrderIdFormat.Match(order.OrderId ?? "");
                if (!match.Success)
                {
                    _logger.LogWarning("CoinGate order {Id} has foreign order_id {OrderId}, skipping reconciliation", order.Id, order.OrderId);
                    continue;
                }
                var userId = match.Groups["user"].Value;
                var productId = int.Parse(match.Groups["product"].Value);

                var product = await db.TopUpProducts.Where(p => p.Id == productId).FirstOrDefaultAsync(token);
                if (product == null
                    || !string.Equals(order.PriceCurrency, receiveCurrency, StringComparison.OrdinalIgnoreCase)
                    || order.PriceAmount != product.Price)
                {
                    Unreconcilable.Inc();
                    _logger.LogWarning(
                        "CoinGate order {Id} ({OrderId}) of user {UserId} cannot be reconciled automatically: paid {Amount} {Currency}, product {ProductId} costs {ProductPrice} (receive currency {ReceiveCurrency})",
                        order.Id, order.OrderId, userId, order.PriceAmount, order.PriceCurrency, productId, product?.Price, receiveCurrency);
                    continue;
                }

                var processed = await processor.ProcessPaidOrderAsync(
                    CoinGateOrder.FromResponse(order),
                    userId, productId.ToString(), (int)product.Cost, "reconciliation");
                if (processed)
                {
                    recorded++;
                    Reconciled.Inc();
                    _logger.LogWarning("CoinGate order {Id} ({OrderId}) paid at {PaidAt} was not recorded by the callback; recorded by reconciliation",
                        order.Id, order.OrderId, order.PaidAt);
                }
            }
            catch (TransactionService.DupplicateTransactionException)
            {
                _logger.LogInformation("CoinGate order {Id} already recorded", order.Id);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CoinGate reconciliation failed for order {Id} ({OrderId})", order.Id, order.OrderId);
            }
        }
        return recorded;
    }
}
