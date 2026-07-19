using System;
using System.Collections.Generic;
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
                    .Select(r => new DetailedTransactionExport
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
                        BuyerEmail = r.BuyerEmail
                    })
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
        /// <returns>Transactions in requested format</returns>
        [HttpGet]
        [Route("transactions/export")]
        public async Task<IActionResult> ExportTransactions(
            [FromQuery] DateTime startDate,
            [FromQuery] DateTime endDate,
            [FromQuery] string provider = null,
            [FromQuery] string country = null,
            [FromQuery] string format = "json"
        )
        {
            startDate = AsUtc(startDate);
            endDate = AsUtc(endDate);

            if (startDate > endDate)
                return BadRequest("startDate must be before or equal to endDate");

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

                var results = await query
                    .OrderByDescending(r => r.PaidAt)
                    .Select(r => new DetailedTransactionExport
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
                        BuyerEmail = r.BuyerEmail
                    })
                    .ToListAsync();

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
                "ExternalReference,ExternalTransactionId,Locale,IsSubscription,BuyerEmail");

            foreach (var tx in transactions)
            {
                var line = $"\"{tx.TransactionId}\"," +
                          $"\"{tx.UserId}\"," +
                          $"\"{tx.UserCountry}\"," +
                          $"\"{tx.ZipCode}\"," +
                          $"\"{tx.State}\"," +
                          $"\"{tx.Provider}\"," +
                          $"\"{tx.PaymentMethod}\"," +
                          $"{tx.Amount}," +
                          $"{tx.Subtotal}," +
                          $"{tx.DiscountAmount}," +
                          $"{tx.TaxAmount}," +
                          $"{tx.TaxRate}," +
                          $"{tx.TaxRemittedByProcessor}," +
                          $"{tx.NetAmount}," +
                          $"{tx.ProcessorFee}," +
                          $"\"{tx.CreatorCode}\"," +
                          $"{tx.CreatorCodeDiscount}," +
                          $"\"{tx.Currency}\"," +
                          $"\"{tx.ProductId}\"," +
                          $"{tx.CoinAmount}," +
                          $"\"{tx.Timestamp:yyyy-MM-dd HH:mm:ss}\"," +
                          $"\"{tx.Status}\"," +
                          $"\"{tx.ExternalReference}\"," +
                          $"\"{tx.ExternalTransactionId}\"," +
                          $"\"{tx.Locale}\"," +
                          $"{tx.IsSubscriptionPayment}," +
                          $"\"{tx.BuyerEmail}\"";
                csv.AppendLine(line);
            }

            var content = csv.ToString();
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);

            return File(bytes, "text/csv", $"transactions_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv");
        }
    }
}