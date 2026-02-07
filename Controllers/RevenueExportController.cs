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
    /// Handles revenue export and compliance reporting
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
        /// Export aggregated payment revenue by country, provider, and time range
        /// Useful for compliance reporting (e.g., quarterly crypto revenue by country)
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
            // Validate date range
            if (startDate > endDate)
            {
                return BadRequest("startDate must be before or equal to endDate");
            }

            if (endDate > DateTime.UtcNow)
            {
                return BadRequest("endDate cannot be in the future");
            }

            try
            {
                // Query payment requests with user data
                var query = _db.PaymentRequests
                    .Include(pr => pr.User)
                    .Include(pr => pr.ProductId)
                    .Where(pr => pr.State == PaymentRequest.Status.CONFIRMED
                        && pr.UpdatedAt >= startDate
                        && pr.UpdatedAt <= endDate);

                // Apply optional filters
                if (!string.IsNullOrWhiteSpace(provider))
                {
                    query = query.Where(pr => pr.Provider == provider);
                }

                if (!string.IsNullOrWhiteSpace(country))
                {
                    query = query.Where(pr => pr.User.Country == country);
                }

                // Build the aggregation
                var results = await query
                    .GroupBy(pr => new
                    {
                        pr.User.Country,
                        pr.Provider,
                        // Group by approximate currency - if not available, default to requested or "USD"
                        Currency = currency ?? "USD"
                    })
                    .Select(g => new RevenueExport
                    {
                        Country = g.Key.Country ?? "UNKNOWN",
                        Provider = g.Key.Provider,
                        Currency = g.Key.Currency,
                        TotalAmount = g.Sum(pr => pr.Amount),
                        TransactionCount = g.Count(),
                        PeriodStart = startDate,
                        PeriodEnd = endDate
                    })
                    .OrderBy(r => r.Country)
                    .ThenBy(r => r.Provider)
                    .ToListAsync();

                _logger.LogInformation(
                    $"Revenue export generated: {results.Count} groups, period {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}");

                return Ok(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating revenue export");
                return StatusCode(500, new { error = "Failed to generate revenue export" });
            }
        }

        /// <summary>
        /// Export aggregated crypto payment revenue by country and quarter
        /// Specialized endpoint for quarterly compliance reporting
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
            // Validate quarter
            if (quarter < 1 || quarter > 4)
            {
                return BadRequest("Quarter must be between 1 and 4");
            }

            if (year < 2020 || year > DateTime.UtcNow.Year)
            {
                return BadRequest("Invalid year");
            }

            // Calculate start and end dates for the quarter
            var startMonth = (quarter - 1) * 3 + 1;
            var endMonth = startMonth + 2;
            var startDate = new DateTime(year, startMonth, 1, 0, 0, 0, DateTimeKind.Utc);
            var endDate = new DateTime(year, endMonth, DateTime.DaysInMonth(year, endMonth), 23, 59, 59, DateTimeKind.Utc);

            // Filter for crypto providers (CoinGate is the main crypto provider in your system)
            var cryptoProviders = new[] { "coingate", "crypto" };

            try
            {
                var results = await _db.PaymentRequests
                    .Include(pr => pr.User)
                    .Include(pr => pr.ProductId)
                    .Where(pr => pr.State == PaymentRequest.Status.CONFIRMED
                        && pr.UpdatedAt >= startDate
                        && pr.UpdatedAt <= endDate
                        && cryptoProviders.Contains(pr.Provider))
                    .GroupBy(pr => new
                    {
                        pr.User.Country
                    })
                    .Select(g => new RevenueExport
                    {
                        Country = g.Key.Country ?? "UNKNOWN",
                        Provider = "crypto",
                        Currency = "USD", // Typically crypto is denominated in fiat equivalent
                        TotalAmount = g.Sum(pr => pr.Amount),
                        TransactionCount = g.Count(),
                        PeriodStart = startDate,
                        PeriodEnd = endDate
                    })
                    .OrderBy(r => r.Country)
                    .ToListAsync();

                _logger.LogInformation(
                    $"Crypto quarterly report generated: Q{quarter} {year}, {results.Count} countries");

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
                var providers = await _db.PaymentRequests
                    .Where(pr => pr.Provider != null)
                    .Select(pr => pr.Provider)
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
                var countries = await _db.PaymentRequests
                    .Include(pr => pr.User)
                    .Where(pr => pr.User.Country != null)
                    .Select(pr => pr.User.Country)
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
        /// Export detailed transaction data with full information per transaction
        /// Includes: user ID, country, provider, amount, timestamp, product, status
        /// Useful for compliance audits and detailed transaction reporting
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
            // Validate date range
            if (startDate > endDate)
            {
                return BadRequest("startDate must be before or equal to endDate");
            }

            if (endDate > DateTime.UtcNow)
            {
                return BadRequest("endDate cannot be in the future");
            }

            // Validate and cap limit
            if (limit < 1 || limit > 5000)
            {
                limit = Math.Min(Math.Max(limit, 1), 5000);
            }

            if (offset < 0)
            {
                offset = 0;
            }

            try
            {
                // Query payment requests with all related data
                var query = _db.PaymentRequests
                    .Include(pr => pr.User)
                    .Include(pr => pr.ProductId)
                    .Where(pr => pr.State == PaymentRequest.Status.CONFIRMED
                        && pr.UpdatedAt >= startDate
                        && pr.UpdatedAt <= endDate);

                // Apply optional filters
                if (!string.IsNullOrWhiteSpace(provider))
                {
                    query = query.Where(pr => pr.Provider == provider);
                }

                if (!string.IsNullOrWhiteSpace(country))
                {
                    query = query.Where(pr => pr.User.Country == country);
                }

                if (!string.IsNullOrWhiteSpace(userId))
                {
                    query = query.Where(pr => pr.User.ExternalId == userId);
                }

                // Execute query with pagination and build result
                var results = await query
                    .OrderByDescending(pr => pr.UpdatedAt)
                    .Skip(offset)
                    .Take(limit)
                    .Select(pr => new DetailedTransactionExport
                    {
                        TransactionId = pr.Id.ToString(),
                        UserId = pr.User.ExternalId,
                        UserCountry = pr.User.Country ?? "UNKNOWN",
                        Provider = pr.Provider,
                        Amount = pr.Amount,
                        Currency = "USD", // Default, can be enhanced to track actual currency per provider
                        ProductId = pr.ProductId.Slug,
                        Timestamp = pr.UpdatedAt,
                        Status = pr.State.ToString(),
                        ExternalReference = pr.SessionId,
                        Locale = pr.Locale
                    })
                    .ToListAsync();

                _logger.LogInformation(
                    $"Exported {results.Count} detailed transactions, period {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}");

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
            try
            {
                var query = _db.PaymentRequests
                    .Include(pr => pr.User)
                    .Include(pr => pr.ProductId)
                    .Where(pr => pr.State == PaymentRequest.Status.CONFIRMED
                        && pr.UpdatedAt >= startDate
                        && pr.UpdatedAt <= endDate);

                if (!string.IsNullOrWhiteSpace(provider))
                {
                    query = query.Where(pr => pr.Provider == provider);
                }

                if (!string.IsNullOrWhiteSpace(country))
                {
                    query = query.Where(pr => pr.User.Country == country);
                }

                var results = await query
                    .OrderByDescending(pr => pr.UpdatedAt)
                    .Select(pr => new DetailedTransactionExport
                    {
                        TransactionId = pr.Id.ToString(),
                        UserId = pr.User.ExternalId,
                        UserCountry = pr.User.Country ?? "UNKNOWN",
                        Provider = pr.Provider,
                        Amount = pr.Amount,
                        Currency = "USD",
                        ProductId = pr.ProductId.Slug,
                        Timestamp = pr.UpdatedAt,
                        Status = pr.State.ToString(),
                        ExternalReference = pr.SessionId,
                        Locale = pr.Locale
                    })
                    .ToListAsync();

                if (format.ToLower() == "csv")
                {
                    return ExportAsCsv(results);
                }

                return Ok(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error exporting transactions");
                return StatusCode(500, new { error = "Failed to export transactions" });
            }
        }

        /// <summary>
        /// Helper method to export transactions as CSV
        /// </summary>
        private IActionResult ExportAsCsv(List<DetailedTransactionExport> transactions)
        {
            var csv = new System.Text.StringBuilder();
            
            // Add header
            csv.AppendLine("TransactionId,UserId,UserCountry,Provider,Amount,Currency,ProductId,Timestamp,Status,ExternalReference,Locale");
            
            // Add rows
            foreach (var tx in transactions)
            {
                var line = $"\"{tx.TransactionId}\"," +
                          $"\"{tx.UserId}\"," +
                          $"\"{tx.UserCountry}\"," +
                          $"\"{tx.Provider}\"," +
                          $"{tx.Amount}," +
                          $"\"{tx.Currency}\"," +
                          $"\"{tx.ProductId}\"," +
                          $"\"{tx.Timestamp:yyyy-MM-dd HH:mm:ss}\"," +
                          $"\"{tx.Status}\"," +
                          $"\"{tx.ExternalReference}\"," +
                          $"\"{tx.Locale}\"";
                csv.AppendLine(line);
            }

            var content = csv.ToString();
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            
            return File(bytes, "text/csv", $"transactions_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv");
        }    }
}