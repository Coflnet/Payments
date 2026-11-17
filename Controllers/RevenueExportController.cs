using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Payments.Controllers
{
    /// <summary>
    /// Handles revenue export and compliance reporting.
    /// All endpoints query the PaymentRecords table which tracks exact amounts
    /// paid, taxes, fees, discounts, and creator codes per transaction.
    /// </summary>
    [ApiController]
    [Route("[controller]")]
    public class RevenueExportController : ControllerBase
    {
        private readonly ILogger<RevenueExportController> _logger;
        private readonly PaymentContext _db;

        public RevenueExportController(
            ILogger<RevenueExportController> logger,
            PaymentContext context
        )
        {
            _logger = logger;
            _db = context;
        }

        /// <summary>
        /// Export aggregated payment revenue by country, provider, and time range.
        /// Now uses PaymentRecords for accurate amounts including discounts, taxes, and fees.
        /// </summary>
        /// <param name="startDate">Start of the time range (UTC)</param>
        /// <param name="endDate">End of the time range (UTC)</param>
        /// <param name="provider">Filter by payment provider slug (optional, e.g. "coingate", "stripe")</param>
        /// <param name="country">Filter by country code ISO 3166-1 alpha-2 (optional)</param>
        /// <param name="currency">Filter by currency code ISO 4217 (optional)</param>
        /// <returns>List of aggregated revenue data grouped by country and provider</returns>
        [HttpGet]
        [Route("summary")]
        public async Task<ActionResult<List<RevenueExport>>> GetRevenueSummary(
            [FromQuery] DateTime startDate,
            [FromQuery] DateTime endDate,
            [FromQuery] string provider = null,
            [FromQuery] string country = null,
            [FromQuery] string currency = null
        )
        {
            startDate = AsUtc(startDate);
            endDate = AsUtc(endDate);

            if (startDate > endDate)
                return BadRequest("startDate must be before or equal to endDate");
            if (endDate > DateTime.UtcNow)
                return BadRequest("endDate cannot be in the future");

            try
            {
                var query = _db.PaymentRecords
                    .Where(r => r.Status == PaymentRecordStatus.Confirmed
                        && r.PaidAt >= startDate
                        && r.PaidAt <= endDate);

                if (!string.IsNullOrWhiteSpace(provider))
                    query = query.Where(r => r.Provider == provider);
                if (!string.IsNullOrWhiteSpace(country))
                    query = query.Where(r => r.Country == country);
                if (!string.IsNullOrWhiteSpace(currency))
                    query = query.Where(r => r.Currency == currency);

                var results = await query
                    .GroupBy(r => new
                    {
                        r.Country,
                        r.Provider,
                        r.Currency,
                        r.TaxRemittedByProcessor
                    })
                    .Select(g => new RevenueExport
                    {
                        Country = g.Key.Country ?? "UNKNOWN",
                        Provider = g.Key.Provider,
                        Currency = g.Key.Currency,
                        TaxRemittedByProcessor = g.Key.TaxRemittedByProcessor,
                        TotalAmount = g.Sum(r => r.GrossAmount),
                        TotalTax = g.Sum(r => r.TaxAmount),
                        TotalNet = g.Sum(r => r.NetAmount),
                        TotalFees = g.Sum(r => r.ProcessorFee),
                        TotalDiscount = g.Sum(r => r.DiscountAmount),
                        TransactionCount = g.Count(),
                        PeriodStart = startDate,
                        PeriodEnd = endDate
                    })
                    .OrderBy(r => r.Country)
                    .ThenBy(r => r.Provider)
                    .ToListAsync();

                _logger.LogInformation(
                    "Revenue export generated: {Count} groups, period {Start:yyyy-MM-dd} to {End:yyyy-MM-dd}",
                    results.Count, startDate, endDate);

                return Ok(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating revenue export");
                return StatusCode(500, new { error = "Failed to generate revenue export" });
            }
        }

        /// <summary>
        /// Export aggregated crypto payment revenue by country and quarter.
        /// Specialized endpoint for quarterly compliance reporting.
        /// </summary>
        /// <param name="year">Year for the report (e.g., 2026)</param>
        /// <param name="quarter">Quarter number (1-4)</param>
        /// <returns>List of crypto payments aggregated by country for the specified quarter</returns>
        [HttpGet]
        [Route("crypto/quarterly")]
        public async Task<ActionResult<List<RevenueExport>>> GetCryptoQuarterlyRevenue(
            [FromQuery] int year,
            [FromQuery] int quarter
        )
        {
            if (quarter < 1 || quarter > 4)
                return BadRequest("Quarter must be between 1 and 4");
            if (year < 2020 || year > DateTime.UtcNow.Year)
                return BadRequest("Invalid year");

            var startMonth = (quarter - 1) * 3 + 1;
            var endMonth = startMonth + 2;
            var startDate = new DateTime(year, startMonth, 1, 0, 0, 0, DateTimeKind.Utc);
            var endDate = new DateTime(year, endMonth, DateTime.DaysInMonth(year, endMonth), 23, 59, 59, DateTimeKind.Utc);

            var cryptoProviders = new[] { "coingate", "crypto" };

            try
            {
                var results = await _db.PaymentRecords
                    .Where(r => r.Status == PaymentRecordStatus.Confirmed
                        && r.PaidAt >= startDate
                        && r.PaidAt <= endDate
                        && cryptoProviders.Contains(r.Provider))
                    .GroupBy(r => new { r.Country, r.Currency })
                    .Select(g => new RevenueExport
                    {
                        Country = g.Key.Country ?? "UNKNOWN",
                        Provider = "crypto",
                        Currency = g.Key.Currency,
                        TotalAmount = g.Sum(r => r.GrossAmount),
                        TotalTax = g.Sum(r => r.TaxAmount),
                        TotalNet = g.Sum(r => r.NetAmount),
                        TotalFees = g.Sum(r => r.ProcessorFee),
                        TotalDiscount = g.Sum(r => r.DiscountAmount),
                        TransactionCount = g.Count(),
                        PeriodStart = startDate,
                        PeriodEnd = endDate
                    })
                    .OrderBy(r => r.Country)
                    .ToListAsync();

                _logger.LogInformation(
                    "Crypto quarterly report generated: Q{Quarter} {Year}, {Count} countries",
                    quarter, year, results.Count);

                return Ok(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating crypto quarterly revenue report");
                return StatusCode(500, new { error = "Failed to generate crypto quarterly report" });
            }
        }

        /// <summary>
        /// Get list of available payment providers in the system
        /// </summary>
        /// <returns>List of distinct provider slugs</returns>
        [HttpGet]
        [Route("providers")]
        public async Task<ActionResult<List<string>>> GetAvailableProviders()
        {
            try
            {
                var providers = await _db.PaymentRecords
                    .Where(r => r.Provider != null)
                    .Select(r => r.Provider)
                    .Distinct()
                    .OrderBy(p => p)
                    .ToListAsync();

                return Ok(providers);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving available providers");
                return StatusCode(500, new { error = "Failed to retrieve providers" });
            }
        }

        /// <summary>
        /// Get list of countries with recorded payments
        /// </summary>
        /// <returns>List of distinct ISO 3166-1 alpha-2 country codes</returns>
        [HttpGet]
        [Route("countries")]
        public async Task<ActionResult<List<string>>> GetAvailableCountries()
        {
            try
            {
                var countries = await _db.PaymentRecords
                    .Where(r => r.Country != null)
                    .Select(r => r.Country)
                    .Distinct()
                    .OrderBy(c => c)
                    .ToListAsync();

                return Ok(countries);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving available countries");
                return StatusCode(500, new { error = "Failed to retrieve countries" });
            }
        }

        /// <summary>
        /// Export detailed transaction data from PaymentRecords.
        /// Includes: exact amounts paid, taxes, fees, discounts, creator codes, country, ZIP.
        /// </summary>
        /// <param name="startDate">Start of the time range (UTC)</param>
        /// <param name="endDate">End of the time range (UTC)</param>
        /// <param name="provider">Filter by payment provider slug (optional)</param>
        /// <param name="country">Filter by user country (optional)</param>
        /// <param name="userId">Filter by specific user ID (optional)</param>
        /// <param name="offset">Pagination offset (default 0)</param>
        /// <param name="limit">Pagination limit (default 1000, max 5000)</param>
        /// <returns>List of detailed transaction data</returns>
        [HttpGet]
        [Route("transactions")]
        public async Task<ActionResult<List<DetailedTransactionExport>>> GetDetailedTransactions(
            [FromQuery] DateTime startDate,
            [FromQuery] DateTime endDate,
            [FromQuery] string provider = null,
            [FromQuery] string country = null,
            [FromQuery] string userId = null,
            [FromQuery] int offset = 0,
            [FromQuery] int limit = 1000
        )
        {
            startDate = AsUtc(startDate);
            endDate = AsUtc(endDate);

            if (startDate > endDate)
                return BadRequest("startDate must be before or equal to endDate");
            if (endDate > DateTime.UtcNow)
                return BadRequest("endDate cannot be in the future");

            limit = Math.Min(Math.Max(limit, 1), 5000);
            if (offset < 0) offset = 0;

            try
            {
                var query = _db.PaymentRecords
                    .Where(r => r.PaidAt >= startDate && r.PaidAt <= endDate);

                if (!string.IsNullOrWhiteSpace(provider))
                    query = query.Where(r => r.Provider == provider);
                if (!string.IsNullOrWhiteSpace(country))
                    query = query.Where(r => r.Country == country);
                if (!string.IsNullOrWhiteSpace(userId))
                    query = query.Where(r => r.ExternalUserId == userId);

                var results = await query
                    .OrderByDescending(r => r.PaidAt)
                    .Skip(offset)
                    .Take(limit)
                    .Select(ExportProjection)
                    .ToListAsync();

                _logger.LogInformation(
                    "Exported {Count} detailed transactions, period {Start:yyyy-MM-dd} to {End:yyyy-MM-dd}",
                    results.Count, startDate, endDate);

                return Ok(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error exporting detailed transactions");
                return StatusCode(500, new { error = "Failed to export transactions" });
            }
        }

        /// <summary>
        /// Get detailed transaction data with optional CSV export format
        /// </summary>
        /// <param name="startDate">Start date</param>
        /// <param name="endDate">End date</param>
        /// <param name="provider">Filter by provider</param>
        /// <param name="country">Filter by country</param>
        /// <param name="format">Export format: "json" (default) or "csv"</param>
        /// <param name="includeAllStatuses">false (default): confirmed payments only. true: payment records of every status
        /// (Confirmed, Refunded, PartiallyRefunded, Disputed) plus captured payments marked RefundPending that have no record</param>
        /// <returns>Transactions in requested format</returns>
        [HttpGet]
        [Route("transactions/export")]
        public async Task<IActionResult> ExportTransactions(
            [FromQuery] DateTime startDate,
            [FromQuery] DateTime endDate,
            [FromQuery] string provider = null,
            [FromQuery] string country = null,
            [FromQuery] string format = "json",
            [FromQuery] bool includeAllStatuses = false
        )
        {
            startDate = AsUtc(startDate);
            endDate = AsUtc(endDate);

            if (startDate > endDate)
                return BadRequest("startDate must be before or equal to endDate");

            try
            {
                var query = _db.PaymentRecords
                    .Where(r => (includeAllStatuses || r.Status == PaymentRecordStatus.Confirmed)
                        && r.PaidAt >= startDate
                        && r.PaidAt <= endDate);

                if (!string.IsNullOrWhiteSpace(provider))
                    query = query.Where(r => r.Provider == provider);
                if (!string.IsNullOrWhiteSpace(country))
                    query = query.Where(r => r.Country == country);

                var results = await query
                    .OrderByDescending(r => r.PaidAt)
                    .Select(ExportProjection)
                    .ToListAsync();
                if (includeAllStatuses)
                    results.AddRange(await PendingRefundExports(startDate, endDate, provider, country));

                if (format.ToLower() == "csv")
                    return ExportAsCsv(results);

                return Ok(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error exporting transactions");
                return StatusCode(500, new { error = "Failed to export transactions" });
            }
        }

        /// <summary>
        /// The export row of a payment record. Subscription mode: Stripe/PayPal sales are "direct" (Coflnet remits the
        /// VAT) or "managed" (Stripe Managed Payments, tax remitted by Stripe); subscription payments of other providers carry
        /// the provider ("lemonsqueezy", "googlepay": their merchant of record remits the tax); not a subscription payment: empty.
        /// PayPal stores the payer's country in BillingCountry; it is exported as PayerCountry (BillingCountry is Stripe's billing
        /// address only). Status is the PaymentRecordStatus name.
        /// </summary>
        private static readonly System.Linq.Expressions.Expression<Func<PaymentRecord, DetailedTransactionExport>> ExportProjection = r =>
            new DetailedTransactionExport
            {
                TransactionId = r.Id.ToString(),
                UserId = r.ExternalUserId,
                UserCountry = r.Country ?? "UNKNOWN",
                ZipCode = r.ZipCode,
                State = r.State,
                Provider = r.Provider,
                Amount = r.GrossAmount,
                Subtotal = r.Subtotal,
                DiscountAmount = r.DiscountAmount,
                TaxAmount = r.TaxAmount,
                TaxRate = r.TaxRate,
                TaxRemittedByProcessor = r.TaxRemittedByProcessor,
                NetAmount = r.NetAmount,
                ProcessorFee = r.ProcessorFee,
                CreatorCode = r.CreatorCode,
                CreatorCodeDiscount = r.CreatorCodeDiscount,
                Currency = r.Currency,
                ProductId = r.ProductSlug,
                CoinAmount = r.CoinAmount,
                Timestamp = r.PaidAt,
                Status = r.Status.ToString(),
                ExternalReference = r.ExternalOrderId,
                ExternalTransactionId = r.ExternalTransactionId,
                Locale = r.Locale,
                PaymentMethod = r.PaymentMethod,
                IsSubscriptionPayment = r.IsSubscriptionPayment,
                BuyerEmail = r.BuyerEmail,
                CheckoutCountry = r.CheckoutCountry,
                BillingCountry = r.Provider == "paypal" ? null : r.BillingCountry,
                PaymentMethodCountry = r.PaymentMethodCountry,
                PayerCountry = r.Provider == "paypal" ? r.BillingCountry : null,
                IpAddress = r.IpAddress,
                CheckoutRegion = r.CheckoutRegion,
                SubscriptionMode = !r.IsSubscriptionPayment ? null
                    : r.Provider == "stripe" ? (r.TaxRemittedByProcessor ? "managed" : "direct")
                    : r.Provider == "paypal" ? "direct"
                    : r.Provider
            };

        /// <summary>
        /// Payments the provider captured but this service refused to fulfil (state REFUND_PENDING, e.g. a direct sale to a
        /// country we do not sell to, a first invoice whose billing address and card contradict the checkout country, a sale
        /// of an unknown subscription) have no PaymentRecord: they are only stored as PaymentRequests keyed by the provider's
        /// payment (sale/invoice) id. Listed as status RefundPending when no record exists for that payment id.
        /// The request does not store the provider's sale time: Timestamp (and the date filter) is when the payment was marked,
        /// normally by the payment's webhook right after the sale. One-time checkouts marked by earlier releases kept no
        /// update time (UpdatedAt unset), so their creation (checkout start) is used instead.
        /// Rejected checkouts (state REJECTED) never took money themselves (their sales appear here as RefundPending), so
        /// they are not exported.
        /// </summary>
        private async Task<List<DetailedTransactionExport>> PendingRefundExports(DateTime startDate, DateTime endDate,
            string provider, string country)
        {
            var query = _db.PaymentRequests
                .Where(r => r.State == PaymentRequest.Status.REFUND_PENDING
                    && !_db.PaymentRecords.Any(p => p.Provider == r.Provider && p.ExternalOrderId == r.SessionId))
                .Select(r => new { Request = r, MarkedAt = r.UpdatedAt > r.CreatedAt ? r.UpdatedAt : r.CreatedAt })
                .Where(r => r.MarkedAt >= startDate && r.MarkedAt <= endDate)
                .Select(r => r.Request);
            if (!string.IsNullOrWhiteSpace(provider))
                query = query.Where(r => r.Provider == provider);
            if (!string.IsNullOrWhiteSpace(country))
                query = query.Where(r => r.Country == country);
            var requests = await query
                .Select(r => new
                {
                    r.Id, r.Provider, r.SessionId, r.Amount, r.Country, r.CheckoutRegion, r.Locale,
                    MarkedAt = r.UpdatedAt > r.CreatedAt ? r.UpdatedAt : r.CreatedAt,
                    Ip = r.CreateOnIp, User = r.User == null ? null : r.User.ExternalId,
                    Product = r.ProductId == null ? null : r.ProductId.Slug,
                    Currency = r.ProductId == null ? null : r.ProductId.CurrencyCode
                }).OrderByDescending(r => r.MarkedAt).ToListAsync();
            return requests.Select(r => new DetailedTransactionExport
            {
                TransactionId = "refund-pending-" + r.Id,
                UserId = r.User,
                UserCountry = r.Country ?? "UNKNOWN",
                Provider = r.Provider,
                Amount = r.Amount,
                Currency = r.Currency?.ToUpperInvariant(),
                ProductId = r.Product,
                Timestamp = r.MarkedAt,
                Status = ExportStatus.FromRequest(PaymentRequest.Status.REFUND_PENDING),
                ExternalReference = r.SessionId,
                Locale = r.Locale,
                CheckoutCountry = r.Country,
                CheckoutRegion = r.CheckoutRegion,
                IpAddress = r.Ip?.ToString()
            }).ToList();
        }

        /// <summary>
        /// Dates bound from the query string carry DateTimeKind.Unspecified unless the caller
        /// spelled out a zone ("2026-05-01" rather than "2026-05-01T00:00:00Z"). Npgsql refuses
        /// those for timestamptz columns, so read a zone-less date as the UTC date it names.
        /// </summary>
        private static DateTime AsUtc(DateTime value)
        {
            return value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
            };
        }

        /// <summary>
        /// Helper method to export transactions as CSV
        /// </summary>
        private IActionResult ExportAsCsv(List<DetailedTransactionExport> transactions)
        {
            var csv = new System.Text.StringBuilder();

            csv.AppendLine("TransactionId,UserId,UserCountry,ZipCode,State,Provider,PaymentMethod," +
                "Amount,Subtotal,DiscountAmount,TaxAmount,TaxRate,TaxRemittedByProcessor," +
                "NetAmount,ProcessorFee,CreatorCode,CreatorCodeDiscount," +
                "Currency,ProductId,CoinAmount,Timestamp,Status," +
                "ExternalReference,ExternalTransactionId,Locale,IsSubscription,BuyerEmail," +
                "CheckoutCountry,BillingCountry,PaymentMethodCountry,PayerCountry,IpAddress,SubscriptionMode,CheckoutRegion");

            foreach (var tx in transactions)
                csv.AppendLine(CsvLine(tx));

            var content = csv.ToString();
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);

            return File(bytes, "text/csv", $"transactions_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv");
        }

        /// <summary>
        /// One CSV row. Numbers and the timestamp are culture invariant (decimal point; "yyyy-MM-dd HH:mm:ss" UTC, unchanged
        /// layout), so a server or test culture such as de-DE can not turn "11.53" into "11,53" and shift every column.
        /// </summary>
        private static string CsvLine(DetailedTransactionExport tx) => string.Join(",",
            Quote(tx.TransactionId), Quote(tx.UserId), Quote(tx.UserCountry), Quote(tx.ZipCode), Quote(tx.State), Quote(tx.Provider),
            Quote(tx.PaymentMethod), Number(tx.Amount), Number(tx.Subtotal), Number(tx.DiscountAmount), Number(tx.TaxAmount),
            Number(tx.TaxRate), tx.TaxRemittedByProcessor.ToString(), Number(tx.NetAmount), Number(tx.ProcessorFee), Quote(tx.CreatorCode),
            Number(tx.CreatorCodeDiscount), Quote(tx.Currency), Quote(tx.ProductId), Number(tx.CoinAmount),
            Quote(tx.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)), Quote(tx.Status),
            Quote(tx.ExternalReference), Quote(tx.ExternalTransactionId), Quote(tx.Locale), tx.IsSubscriptionPayment.ToString(),
            Quote(tx.BuyerEmail), Quote(tx.CheckoutCountry), Quote(tx.BillingCountry), Quote(tx.PaymentMethodCountry),
            Quote(tx.PayerCountry), Quote(tx.IpAddress), Quote(tx.SubscriptionMode), Quote(tx.CheckoutRegion));

        private static string Number(IFormattable value) => value.ToString(null, CultureInfo.InvariantCulture);

        /// <summary>A quoted CSV field; quotes inside are doubled (RFC 4180)</summary>
        private static string Quote(string value) => "\"" + value?.Replace("\"", "\"\"") + "\"";
    }
}
