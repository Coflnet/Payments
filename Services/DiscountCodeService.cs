using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Models.LemonSqueezy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Coflnet.Payments.Services;

/// <summary>
/// Discount codes stored in our own table, used by Stripe, PayPal and CoinGate.
/// Lemon Squeezy checkouts keep validating against Lemon Squeezy.
/// </summary>
public class DiscountCodeService
{
    private readonly PaymentContext db;
    private readonly ILogger<DiscountCodeService> logger;

    public DiscountCodeService(PaymentContext db, ILogger<DiscountCodeService> logger)
    {
        this.db = db;
        this.logger = logger;
    }

    /// <summary>
    /// Validates a code for a checkout. Null when the code is unknown, inactive or outside its validity window;
    /// IsValid=false (with IsSubscriptionOnly) when it only applies to subscriptions.
    /// </summary>
    public async Task<ValidatedDiscount> Validate(string code, bool isSubscription)
    {
        var discount = await Find(code);
        if (discount == null || !discount.IsActive)
            return null;
        if (!IsAvailable(discount))
        {
            logger.LogInformation("Discount code {Code} is outside its validity window or used up", discount.Code);
            return null;
        }
        var validated = ToValidated(discount);
        validated.IsValid = isSubscription || !discount.SubscriptionOnly;
        return validated;
    }

    /// <summary>
    /// The newest active code flagged <see cref="DiscountCode.AutoApply"/> that is inside its validity window
    /// and has redemptions left; null when there is none.
    /// </summary>
    public async Task<DiscountCode> GetActiveAutoApply()
    {
        var now = DateTime.UtcNow;
        var candidates = await db.DiscountCodes
            .Where(c => c.AutoApply && c.IsActive && c.SubscriptionOnly)
            .Where(c => c.StartsAt == null || c.StartsAt <= now)
            .Where(c => c.ExpiresAt == null || c.ExpiresAt > now)
            .Where(c => c.MaxRedemptions == null || c.TimesRedeemed < c.MaxRedemptions)
            .ToListAsync();
        // ordered in memory: CreatedAt ties (and SQLite/Postgres collations) must not decide
        return candidates.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).FirstOrDefault();
    }

    private static bool IsAvailable(DiscountCode discount)
    {
        var now = DateTime.UtcNow;
        return !(discount.StartsAt > now || discount.ExpiresAt < now || discount.TimesRedeemed >= discount.MaxRedemptions);
    }

    /// <summary>
    /// Counts one use of a local code when a checkout is started; false when the code is used up (or unknown).
    /// The conditional update keeps concurrent checkouts within <see cref="DiscountCode.MaxRedemptions"/>.
    /// </summary>
    public async Task<bool> Redeem(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return false;
        var normalized = Normalize(code);
        return await db.DiscountCodes
            .Where(c => c.Code == normalized && (c.MaxRedemptions == null || c.TimesRedeemed < c.MaxRedemptions))
            .ExecuteUpdateAsync(c => c.SetProperty(d => d.TimesRedeemed, d => d.TimesRedeemed + 1)) > 0;
    }

    /// <summary>
    /// Gives back one use counted by <see cref="Redeem"/> when the checkout it was for could not be opened; never below 0.
    /// </summary>
    public async Task Release(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return;
        var normalized = Normalize(code);
        await db.DiscountCodes
            .Where(c => c.Code == normalized && c.TimesRedeemed > 0)
            .ExecuteUpdateAsync(c => c.SetProperty(d => d.TimesRedeemed, d => d.TimesRedeemed - 1));
    }

    /// <summary>
    /// The terms of a code regardless of its validity, used to reprice an existing subscription on a plan switch.
    /// </summary>
    public async Task<ValidatedDiscount> Terms(string code) =>
        await Find(code) is DiscountCode discount ? ToValidated(discount) : null;

    /// <summary>
    /// All codes, newest first
    /// </summary>
    public Task<List<DiscountCode>> List() => db.DiscountCodes.OrderByDescending(c => c.Id).ToListAsync();

    /// <summary>
    /// Creates or replaces the code with the same <see cref="DiscountCode.Code"/>
    /// </summary>
    public async Task<DiscountCode> Upsert(DiscountCode update)
    {
        Validate(update);
        var code = Normalize(update.Code);
        var existing = await db.DiscountCodes.SingleOrDefaultAsync(c => c.Code == code);
        if (existing == null)
        {
            existing = new DiscountCode { Code = code, CreatedAt = DateTime.UtcNow };
            db.DiscountCodes.Add(existing);
        }
        existing.Name = update.Name;
        existing.AmountType = update.AmountType;
        existing.Amount = update.Amount;
        existing.PeriodAmounts = update.PeriodAmounts is { Count: > 0 } ? new(update.PeriodAmounts) : null;
        existing.AutoApply = update.AutoApply;
        existing.IsActive = update.IsActive;
        existing.SubscriptionOnly = update.SubscriptionOnly;
        existing.StartsAt = update.StartsAt;
        existing.ExpiresAt = update.ExpiresAt;
        existing.Duration = update.Duration ?? DiscountCode.EveryRenewal;
        existing.MaxRedemptions = update.MaxRedemptions;
        await db.SaveChangesAsync();
        return existing;
    }

    private static void Validate(DiscountCode update)
    {
        if (string.IsNullOrWhiteSpace(update?.Code) || update.Code.Trim().Length > 50)
            throw new ApiException("A discount code of 1-50 characters is required");
        if (update.AmountType is not ("percent" or "fixed"))
            throw new ApiException("AmountType must be 'percent' or 'fixed'");
        if (update.Amount <= 0 || update.AmountType == "percent" && update.Amount >= 100)
            throw new ApiException("Amount must be positive (and below 100 for percent codes)");
        ValidatePeriodAmounts(update);
        if (update.AutoApply && !update.SubscriptionOnly)
            throw new ApiException("AutoApply codes must be SubscriptionOnly");
        if (update.Duration is not (null or DiscountCode.EveryRenewal or DiscountCode.FirstPaymentOnly))
            throw new ApiException("Duration must be 'forever' or 'once'");
        if (update.MaxRedemptions < 1)
            throw new ApiException("MaxRedemptions must be at least 1 when set");
    }

    private static void ValidatePeriodAmounts(DiscountCode update)
    {
        foreach (var (weeks, amount) in update.PeriodAmounts ?? [])
        {
            if (weeks is not (4 or 13 or 52))
                throw new ApiException("PeriodAmounts keys must be the billing weeks 4, 13 or 52");
            if (amount <= 0 || update.AmountType == "percent" && amount >= 100)
                throw new ApiException("PeriodAmounts must be positive (and below 100 for percent codes)");
        }
    }

    private Task<DiscountCode> Find(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return Task.FromResult<DiscountCode>(null);
        var normalized = Normalize(code);
        return db.DiscountCodes.SingleOrDefaultAsync(c => c.Code == normalized);
    }

    private static string Normalize(string code) => code.Trim().ToUpperInvariant();

    private static ValidatedDiscount ToValidated(DiscountCode discount) => new()
    {
        Code = discount.Code,
        Id = "local-" + discount.Id,
        Amount = discount.Amount,
        PeriodAmounts = discount.PeriodAmounts is { Count: > 0 } ? new(discount.PeriodAmounts) : null,
        ExpiresAt = discount.ExpiresAt,
        AmountType = discount.AmountType,
        Name = discount.Name ?? discount.Code,
        IsValid = true,
        IsSubscriptionOnly = discount.SubscriptionOnly,
        Duration = discount.Duration ?? DiscountCode.EveryRenewal
    };
}
