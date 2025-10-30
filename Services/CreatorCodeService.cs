using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Coflnet.Payments.Services
{
    /// <summary>
    /// Service for managing creator codes and tracking revenue
    /// </summary>
    public class CreatorCodeService
    {
        private readonly ILogger<CreatorCodeService> _logger;
        private readonly PaymentContext _context;

        /// <summary>
        /// Initializes a new instance of CreatorCodeService
        /// </summary>
        public CreatorCodeService(ILogger<CreatorCodeService> logger, PaymentContext context)
        {
            _logger = logger;
            _context = context;
        }

        /// <summary>
        /// Creates a new creator code
        /// </summary>
        /// <param name="code">The code string (e.g., "TECHNO")</param>
        /// <param name="creatorUserId">The creator's user ID</param>
        /// <param name="discountPercent">Discount percentage (e.g., 5 for 5%)</param>
        /// <param name="revenueSharePercent">Revenue share percentage for creator</param>
        /// <param name="expiresAt">Optional expiration date</param>
        /// <param name="maxUses">Optional maximum number of uses</param>
        /// <returns>The created creator code</returns>
        public async Task<CreatorCode> CreateCreatorCodeAsync(
            string code,
            string creatorUserId,
            decimal discountPercent,
            decimal revenueSharePercent,
            DateTime? expiresAt = null,
            int? maxUses = null)
        {
            // Validate code doesn't already exist
            var existing = await _context.CreatorCodes
                .FirstOrDefaultAsync(c => c.Code.ToLower() == code.ToLower());

            if (existing != null)
            {
                throw new ArgumentException($"Creator code '{code}' already exists");
            }

            var creatorCode = new CreatorCode
            {
                Code = code.ToUpper(),
                CreatorUserId = creatorUserId,
                DiscountPercent = discountPercent,
                RevenueSharePercent = revenueSharePercent,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                ExpiresAt = expiresAt,
                MaxUses = maxUses,
                TimesUsed = 0
            };

            _context.CreatorCodes.Add(creatorCode);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Created creator code {Code} for user {UserId} with {Discount}% discount",
                code, creatorUserId, discountPercent);

            return creatorCode;
        }

        /// <summary>
        /// Gets a creator code by code string
        /// </summary>
        /// <param name="code">The code to look up</param>
        /// <returns>The creator code or null if not found</returns>
        public async Task<CreatorCode> GetCreatorCodeAsync(string code)
        {
            return await _context.CreatorCodes
                .FirstOrDefaultAsync(c => c.Code.ToLower() == code.ToLower() && c.IsActive);
        }

        /// <summary>
        /// Validates if a creator code can be used
        /// </summary>
        /// <param name="code">The code to validate</param>
        /// <returns>The creator code if valid, null otherwise</returns>
        public async Task<CreatorCode> ValidateCreatorCodeAsync(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return null;
            }

            var creatorCode = await GetCreatorCodeAsync(code);

            if (creatorCode == null)
            {
                _logger.LogWarning("Creator code {Code} not found or inactive", code);
                return null;
            }

            // Check expiration
            if (creatorCode.ExpiresAt.HasValue && creatorCode.ExpiresAt.Value < DateTime.UtcNow)
            {
                _logger.LogWarning("Creator code {Code} has expired", code);
                return null;
            }

            // Check max uses
            if (creatorCode.MaxUses.HasValue && creatorCode.TimesUsed >= creatorCode.MaxUses.Value)
            {
                _logger.LogWarning("Creator code {Code} has reached max uses", code);
                return null;
            }

            return creatorCode;
        }

        /// <summary>
        /// Records revenue from a creator code usage
        /// </summary>
        /// <param name="creatorCodeId">The creator code ID</param>
        /// <param name="userId">The purchasing user ID</param>
        /// <param name="productId">The product ID</param>
        /// <param name="originalPrice">Original price before discount</param>
        /// <param name="discountAmount">Discount amount applied</param>
        /// <param name="finalPrice">Final price paid</param>
        /// <param name="creatorRevenue">Revenue share for creator</param>
        /// <param name="currency">Currency code</param>
        /// <param name="transactionReference">Payment provider transaction reference</param>
        /// <returns>The created revenue record</returns>
        public async Task<CreatorCodeRevenue> RecordRevenueAsync(
            int creatorCodeId,
            string userId,
            int productId,
            decimal originalPrice,
            decimal discountAmount,
            decimal finalPrice,
            decimal creatorRevenue,
            string currency,
            string transactionReference = null)
        {
            var revenue = new CreatorCodeRevenue
            {
                CreatorCodeId = creatorCodeId,
                UserId = userId,
                ProductId = productId,
                OriginalPrice = originalPrice,
                DiscountAmount = discountAmount,
                FinalPrice = finalPrice,
                CreatorRevenue = creatorRevenue,
                Currency = currency,
                TransactionReference = transactionReference,
                PurchasedAt = DateTime.UtcNow,
                IsPaidOut = false
            };

            _context.CreatorCodeRevenues.Add(revenue);

            // Increment usage counter
            var creatorCode = await _context.CreatorCodes.FindAsync(creatorCodeId);
            if (creatorCode != null)
            {
                creatorCode.TimesUsed++;
                creatorCode.UpdatedAt = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync();

            _logger.LogInformation("Recorded revenue for creator code ID {CodeId}: {Revenue} {Currency}",
                creatorCodeId, creatorRevenue, currency);

            return revenue;
        }

        /// <summary>
        /// Gets net revenue for a creator code in a specific time period
        /// </summary>
        /// <param name="code">The creator code</param>
        /// <param name="startDate">Start of the period</param>
        /// <param name="endDate">End of the period</param>
        /// <returns>Revenue summary</returns>
        public async Task<CreatorRevenueReport> GetRevenueReportAsync(
            string code,
            DateTime startDate,
            DateTime endDate)
        {
            var creatorCode = await _context.CreatorCodes
                .FirstOrDefaultAsync(c => c.Code.ToLower() == code.ToLower());

            if (creatorCode == null)
            {
                throw new ArgumentException($"Creator code '{code}' not found");
            }

            var revenues = await _context.CreatorCodeRevenues
                .Where(r => r.CreatorCodeId == creatorCode.Id
                    && r.PurchasedAt >= startDate
                    && r.PurchasedAt <= endDate)
                .ToListAsync();

            var groupedByCurrency = revenues
                .GroupBy(r => r.Currency)
                .Select(g => new CurrencyRevenue
                {
                    Currency = g.Key,
                    TotalRevenue = g.Sum(r => r.CreatorRevenue),
                    TotalTransactions = g.Count(),
                    PaidOut = g.Where(r => r.IsPaidOut).Sum(r => r.CreatorRevenue),
                    Unpaid = g.Where(r => !r.IsPaidOut).Sum(r => r.CreatorRevenue)
                })
                .ToList();

            return new CreatorRevenueReport
            {
                Code = code,
                CreatorUserId = creatorCode.CreatorUserId,
                StartDate = startDate,
                EndDate = endDate,
                RevenueByCurrency = groupedByCurrency,
                TotalTransactions = revenues.Count
            };
        }

        /// <summary>
        /// Updates a creator code
        /// </summary>
        /// <param name="code">The code to update</param>
        /// <param name="discountPercent">New discount percentage (optional)</param>
        /// <param name="revenueSharePercent">New revenue share percentage (optional)</param>
        /// <param name="isActive">New active status (optional)</param>
        /// <param name="expiresAt">New expiration date (optional)</param>
        /// <param name="maxUses">New max uses (optional)</param>
        /// <returns>The updated creator code</returns>
        public async Task<CreatorCode> UpdateCreatorCodeAsync(
            string code,
            decimal? discountPercent = null,
            decimal? revenueSharePercent = null,
            bool? isActive = null,
            DateTime? expiresAt = null,
            int? maxUses = null)
        {
            var creatorCode = await _context.CreatorCodes
                .FirstOrDefaultAsync(c => c.Code.ToLower() == code.ToLower());

            if (creatorCode == null)
            {
                throw new ArgumentException($"Creator code '{code}' not found");
            }

            if (discountPercent.HasValue)
                creatorCode.DiscountPercent = discountPercent.Value;

            if (revenueSharePercent.HasValue)
                creatorCode.RevenueSharePercent = revenueSharePercent.Value;

            if (isActive.HasValue)
                creatorCode.IsActive = isActive.Value;

            if (expiresAt.HasValue)
                creatorCode.ExpiresAt = expiresAt.Value;

            if (maxUses.HasValue)
                creatorCode.MaxUses = maxUses.Value;

            creatorCode.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            _logger.LogInformation("Updated creator code {Code}", code);

            return creatorCode;
        }

        /// <summary>
        /// Lists all creator codes for a specific creator
        /// </summary>
        /// <param name="creatorUserId">The creator's user ID</param>
        /// <returns>List of creator codes</returns>
        public async Task<List<CreatorCode>> GetCreatorCodesForUserAsync(string creatorUserId)
        {
            return await _context.CreatorCodes
                .Where(c => c.CreatorUserId == creatorUserId)
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync();
        }
    }

    /// <summary>
    /// Revenue report for a creator code
    /// </summary>
    public class CreatorRevenueReport
    {
        /// <summary>
        /// The creator code
        /// </summary>
        public string Code { get; set; }

        /// <summary>
        /// The creator's user ID
        /// </summary>
        public string CreatorUserId { get; set; }

        /// <summary>
        /// Start date of the report period
        /// </summary>
        public DateTime StartDate { get; set; }

        /// <summary>
        /// End date of the report period
        /// </summary>
        public DateTime EndDate { get; set; }

        /// <summary>
        /// Revenue breakdown by currency
        /// </summary>
        public List<CurrencyRevenue> RevenueByCurrency { get; set; }

        /// <summary>
        /// Total number of transactions
        /// </summary>
        public int TotalTransactions { get; set; }
    }

    /// <summary>
    /// Revenue for a specific currency
    /// </summary>
    public class CurrencyRevenue
    {
        /// <summary>
        /// Currency code
        /// </summary>
        public string Currency { get; set; }

        /// <summary>
        /// Total revenue in this currency
        /// </summary>
        public decimal TotalRevenue { get; set; }

        /// <summary>
        /// Number of transactions in this currency
        /// </summary>
        public int TotalTransactions { get; set; }

        /// <summary>
        /// Amount already paid out
        /// </summary>
        public decimal PaidOut { get; set; }

        /// <summary>
        /// Amount not yet paid out
        /// </summary>
        public decimal Unpaid { get; set; }
    }
}
