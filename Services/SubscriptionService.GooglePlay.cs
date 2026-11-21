using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Models.GooglePay;
using Coflnet.Payments.Services.Subscriptions;
using Google;
using Google.Apis.AndroidPublisher.v3.Data;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using User = Coflnet.Payments.Models.User;

namespace Coflnet.Payments.Services;

public partial class SubscriptionService
{
    internal const string GooglePlayPackage = "com.coflnet.sky";
    internal const string GooglePlayManaged = "Managed in Google Play";
    private const string PlayProvider = SubscriptionProviders.GooglePlay;
    private const string PlayOrderPrefix = "googleplay-order-";
    private readonly IGooglePlayPublisher googlePlay;
    private readonly IConfigurationSection googlePlayConfig;

    private string PlayBasePlan => googlePlayConfig?["BASE_PLAN"] ?? "p4w";

    private IReadOnlyDictionary<string, string> PlayPlans =>
        googlePlayConfig?["SUBSCRIPTION_PLANS"] is string json
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new()
            : googlePlayConfig?.GetSection("SUBSCRIPTION_PLANS").GetChildren()
                .ToDictionary(p => p.Key, p => p.Value) ?? new();

    private async Task<TopUpProduct> GooglePlayPlan(string slug)
    {
        if (slug is not ("l_premium" or "l_prem_plus"))
            return null;
        var plan = await PlanBySlug(slug);
        return plan?.SlotCount == 0 && IntervalWeeks(plan.OwnershipSeconds) == 4 ? plan : null;
    }

    internal async Task<GooglePlayEligibility> GooglePlayEligibility(string userId, string productSlug)
    {
        var productId = PlayPlans.FirstOrDefault(p => p.Value == productSlug).Key;
        var plan = productId == null ? null : await GooglePlayPlan(productSlug);
        if (plan == null)
            return new(false, "plan_unavailable", null, null, null);
        var blocking = await ActiveRenewingInLine(context, planLines, userId, plan);
        return blocking == null
            ? new(true, null, productId, PlayBasePlan, null)
            : new(false, "active_subscription", productId, PlayBasePlan, SubscriptionProviders.Normalize(blocking.ProviderSlug));
    }

    internal static async Task<UserSubscription> ActiveRenewingInLine(PaymentContext db, PlanLines lines, string userId, Product plan,
        string provider = null, string excludeId = null, string linkedId = null)
    {
        var renewing = await db.Subscriptions.AsNoTracking().Include(s => s.Product).ThenInclude(p => p.Groups)
            .Where(s => s.User.ExternalId == userId && s.Status != "expired" && s.EndsAt == null && s.Product.SlotCount == 0
                && (s.ProviderSlug != PlayProvider || s.Status != "pending" || db.FiniteTransactions.Any(t =>
                    t.User.Id == s.User.Id && t.SubscriptionId == s.ExternalId && t.Amount < 0))
                && (provider == null || s.ProviderSlug == provider)
                && (excludeId == null || s.ExternalId != excludeId) && (linkedId == null || s.ExternalId != linkedId))
            .ToListAsync();
        if (renewing.Count == 0)
            return null;
        var product = await db.Products.AsNoTracking().Include(p => p.Groups).FirstOrDefaultAsync(p => p.Id == plan.Id);
        return renewing.FirstOrDefault(s => lines.SameLine(s.Product, product ?? plan));
    }

    private static string PlaySubscriptionId(string token) =>
        "gp_" + WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Synchronizes a Google-verified purchase and grants each paid order once.</summary>
    public Task<GooglePlaySubscriptionResult> SyncGooglePlay(string token, string claimedUserId = null) =>
        SyncGooglePlayCore(token, claimedUserId, false, null);

    internal Task<GooglePlaySubscriptionResult> SyncGooglePlayNotification(string token, bool revoked = false, string voidedOrderId = null) =>
        SyncGooglePlayCore(token, null, revoked, voidedOrderId);

    internal Task ReconcileGooglePlay(string id, DateTime cutoff) => WithSubscriptionLock(id, async () =>
    {
        var token = await context.Subscriptions.Where(s => s.ProviderSlug == PlayProvider && s.ExternalId == id
                && s.Status != "expired" && s.UpdatedAt <= cutoff)
            .Select(s => s.ProviderToken).FirstOrDefaultAsync();
        return token == null ? null : await SyncGooglePlayLocked(id, token, null, false, null);
    });

    private async Task<GooglePlaySubscriptionResult> SyncGooglePlayCore(string token, string claimedUserId, bool revoked, string voidedOrderId)
    {
        if (string.IsNullOrWhiteSpace(token))
            return PlayRejected("invalid_purchase");
        var id = PlaySubscriptionId(token);
        if (!await context.Subscriptions.AnyAsync(s => s.ProviderSlug == PlayProvider && s.ExternalId == id))
        {
            // Insert before taking the row lease. Concurrent first deliveries resolve to the same row.
            var first = await GetGooglePlayPurchase(token);
            var binding = await ResolveGooglePlayPurchase(id, first, claimedUserId);
            if (binding.Error != null)
                return PlayRejected(binding.Error);
            await GetOrCreateSubscriptionAsync(new UserSubscription
            {
                ExternalId = id, ProviderSlug = PlayProvider, ProviderToken = token, User = binding.User, Product = binding.Plan,
                Status = "pending", CreatedAt = first.StartTimeDateTimeOffset?.UtcDateTime ?? DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow, RenewsAt = DateTime.UtcNow
            });
        }

        return await WithSubscriptionLock(id, () => SyncGooglePlayLocked(id, token, claimedUserId, revoked, voidedOrderId));
    }

    private async Task<SubscriptionPurchaseV2> GetGooglePlayPurchase(string token)
    {
        try
        {
            return await googlePlay.GetSubscriptionV2(token);
        }
        catch (GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.Gone)
        {
            return null;
        }
    }

    private async Task<(TopUpProduct Plan, User User, string LinkedId, string Error)> ResolveGooglePlayPurchase(
        string id, SubscriptionPurchaseV2 purchase, string claimedUserId, UserSubscription existing = null)
    {
        if (purchase == null)
            return (null, null, null, "invalid_purchase");
        var item = purchase.LineItems?.FirstOrDefault();
        if (!string.IsNullOrEmpty(item?.OfferDetails?.OfferId))
        {
            logger.LogError("Google Play subscription {SubscriptionId} uses an unsupported offer", id);
            return (null, null, null, "plan_unavailable");
        }
        if (GooglePlayState(purchase.SubscriptionState, item?.AutoRenewingPlan?.AutoRenewEnabled == true) == null)
        {
            logger.LogWarning("Ignoring unknown Google Play state for subscription {SubscriptionId}", id);
            return (null, null, null, "unsupported_state");
        }
        var plan = item?.ProductId != null && item.OfferDetails?.BasePlanId == PlayBasePlan
            ? await GooglePlayPlan(PlayPlans.GetValueOrDefault(item.ProductId)) : null;
        if (plan == null)
        {
            logger.LogWarning("Ignoring Google Play subscription {SubscriptionId} with an unsupported product or base plan", id);
            return (null, null, null, "plan_unavailable");
        }
        if (purchase.TestPurchase != null && string.Equals(googlePlayConfig?["ACCEPT_TEST_PURCHASES"], "false", StringComparison.OrdinalIgnoreCase))
            return (null, null, null, "test_purchase_disabled");

        var linkedId = string.IsNullOrEmpty(purchase.LinkedPurchaseToken) ? existing?.PreviousSubscriptionId
            : PlaySubscriptionId(purchase.LinkedPurchaseToken);
        var (remoteUserId, error) = await ResolveGooglePlayPurchaseUserId(id, purchase, linkedId, existing?.User.ExternalId, claimedUserId);
        if (error != null)
            return (null, null, null, error);
        if (remoteUserId == null)
        {
            logger.LogError("Google Play subscription {SubscriptionId} has no bound user", id);
            return (null, null, null, "missing_user");
        }
        if ((claimedUserId != null && remoteUserId != claimedUserId) || (existing != null && remoteUserId != existing.User.ExternalId))
            return (null, null, null, "user_mismatch");
        var user = existing?.User ?? await userService.GetOrCreate(remoteUserId);
        return (plan, user, linkedId, null);
    }

    private async Task<(string UserId, string Error)> ResolveGooglePlayPurchaseUserId(
        string id, SubscriptionPurchaseV2 purchase, string linkedId, string existingUserId, string claimedUserId)
    {
        var userId = purchase.ExternalAccountIdentifiers?.ObfuscatedExternalAccountId;
        if (string.IsNullOrWhiteSpace(userId))
            userId = linkedId == null ? null : (await FindProviderSubscription(PlayProvider, linkedId))?.User.ExternalId;
        if (existingUserId != null)
            return (userId ?? existingUserId, null);

        var resubscribe = purchase.OutOfAppPurchaseContext;
        var expiredAccountId = resubscribe?.ExpiredExternalAccountIdentifiers?.ObfuscatedExternalAccountId;
        if (string.IsNullOrWhiteSpace(expiredAccountId))
            expiredAccountId = null;
        var expiredToken = resubscribe?.ExpiredPurchaseToken;
        var expiredTokenUserId = string.IsNullOrWhiteSpace(expiredToken) ? null
            : (await FindProviderSubscription(PlayProvider, PlaySubscriptionId(expiredToken)))?.User.ExternalId;
        if (expiredAccountId != null && expiredTokenUserId != null && expiredAccountId != expiredTokenUserId)
        {
            logger.LogError("Google Play resubscribe account sources disagree for subscription {SubscriptionId}", id);
            return (null, "user_mismatch");
        }

        userId ??= expiredAccountId ?? expiredTokenUserId;
        if (userId != null || string.IsNullOrWhiteSpace(claimedUserId))
            return (userId, null);
        logger.LogWarning("Binding Google Play subscription {SubscriptionId} to the app account because Play supplied no account identifier", id);
        return (claimedUserId, null);
    }

    private async Task<GooglePlaySubscriptionResult> SyncGooglePlayLocked(string id, string token, string claimedUserId, bool revoked, string voidedOrderId)
    {
        var local = await FindProviderSubscription(PlayProvider, id);
        if (claimedUserId != null && local.User.ExternalId != claimedUserId)
            return PlayRejected("user_mismatch");
        var purchase = await GetGooglePlayPurchase(token);
        var item = purchase?.LineItems?.FirstOrDefault();
        var latestOrder = item?.LatestSuccessfulOrderId;
        if (revoked && purchase is { SubscriptionState: "SUBSCRIPTION_STATE_EXPIRED", CanceledStateContext: not null })
        {
            await EndGooglePlayAccess(local);
            // The cancel context also covers ordinary expiration. Verify the payment reversal separately.
            latestOrder ??= await LatestGrantedGooglePlayOrder(local);
            if (latestOrder != null && await googlePlay.IsVoidedPurchase(latestOrder, token))
            {
                await RefundGooglePlayOrder(local, latestOrder);
                return new(false, local.Status, null);
            }
        }
        if (voidedOrderId != null)
        {
            latestOrder ??= await LatestGrantedGooglePlayOrder(local);
            var latestVoided = voidedOrderId == latestOrder;
            if (latestVoided || await IsPeriodGrant(local, SubscriptionProviders.GrantReference(PlayProvider, voidedOrderId)))
                await RefundGooglePlayOrder(local, voidedOrderId);
            if (latestVoided)
            {
                await EndGooglePlayAccess(local);
                return new(false, local.Status, null);
            }
        }
        if (purchase == null)
        {
            local.Status = "expired";
            local.UpdatedAt = DateTime.UtcNow;
            local.EndsAt ??= local.UpdatedAt;
            await context.SaveChangesAsync();
            return PlayRejected("invalid_purchase", local.Status);
        }
        var binding = await ResolveGooglePlayPurchase(id, purchase, claimedUserId, local);
        if (binding.Error != null)
            return PlayRejected(binding.Error);

        return await ApplyGooglePlayPurchase(local, token, purchase, item, binding.Plan, binding.LinkedId);
    }

    private async Task<GooglePlaySubscriptionResult> ApplyGooglePlayPurchase(UserSubscription local, string token,
        SubscriptionPurchaseV2 purchase, SubscriptionPurchaseLineItem item, TopUpProduct plan, string linkedId)
    {
        var order = item.LatestSuccessfulOrderId;
        var state = GooglePlayState(purchase.SubscriptionState, item.AutoRenewingPlan?.AutoRenewEnabled == true);
        if (await WasGooglePlayReplaced(local) || (order != null && await context.RefundedSubscriptionInvoices.AnyAsync(r => r.InvoiceId == order)))
        {
            await EndGooglePlayAccess(local);
            return new(false, local.Status, null);
        }

        var expiry = item.ExpiryTimeDateTimeOffset?.UtcDateTime;
        var price = item.AutoRenewingPlan?.RecurringPrice;
        var amount = (price?.Units ?? 0) + (price?.Nanos ?? 0) / 1_000_000_000m;
        ApplyGooglePlayDetails(local, token, purchase.RegionCode, item.ProductId, plan, linkedId, price?.CurrencyCode, amount);
        if (!GooglePlayRegionAllowed(purchase.RegionCode) && await LatestGrantedGooglePlayOrder(local) == null)
        {
            await EndGooglePlayAccess(local);
            return PlayRejected("region_not_allowed", local.Status);
        }

        var wasSuspended = local.Status is "unpaid" or "paused";
        local.Status = state;
        local.EndsAt = state is "cancelled" or "expired" ? expiry ?? DateTime.UtcNow : null;
        if (expiry is DateTime end)
            local.RenewsAt = end;
        var grants = order != null && state is ("active" or "cancelled" or "past_due");
        if (grants)
        {
            await GrantGooglePlayOrder(local, plan, purchase, order, amount);
            await CloseLinkedGooglePlay(local);
        }
        await AlignGooglePlayAccess(local, expiry, grants, wasSuspended);
        await context.SaveChangesAsync();

        if (grants && purchase.AcknowledgementState == "ACKNOWLEDGEMENT_STATE_PENDING")
            await googlePlay.AcknowledgeSubscription(item.ProductId, token);
        return new(grants, state, null);
    }

    private static void ApplyGooglePlayDetails(UserSubscription local, string token, string region, string productId,
        TopUpProduct plan, string linkedId, string currency, decimal amount)
    {
        local.Product = plan;
        local.ProviderPlanId = productId;
        local.ProviderToken = token;
        local.PreviousSubscriptionId = linkedId;
        local.ManagedPayments = true;
        local.CheckoutCountry = region;
        local.Currency = currency;
        local.PriceCents = currency == null ? null : StripeAmounts.ToMinorUnits(amount, currency);
        local.PaymentAmount = currency == null ? null : amount.ToString(CultureInfo.InvariantCulture) + " " + currency;
        local.UpdatedAt = DateTime.UtcNow;
    }

    private bool GooglePlayRegionAllowed(string region)
    {
        var allowed = googlePlayConfig?["ALLOWED_REGIONS"]?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return allowed == null || allowed.Length == 0 || allowed.Contains(region, StringComparer.OrdinalIgnoreCase);
    }

    private async Task GrantGooglePlayOrder(UserSubscription local, TopUpProduct plan, SubscriptionPurchaseV2 purchase, string order, decimal amount)
    {
        var reference = SubscriptionProviders.GrantReference(PlayProvider, order);
        var first = await IsFirstGrantedPayment(local, reference);
        var paidAt = first ? purchase.StartTimeDateTimeOffset?.UtcDateTime : null;
        var payment = purchase.TestPurchase != null ? null : new PaymentRecord
        {
            User = local.User, ExternalUserId = local.User.ExternalId, Country = local.CheckoutCountry,
            Provider = PlayProvider, PaymentMethod = PlayProvider, ExternalOrderId = order, ExternalTransactionId = local.ExternalId,
            ProductId = plan.Id, ProductSlug = plan.Slug, CoinAmount = (long)plan.Cost,
            GrossAmount = amount, Subtotal = amount, NetAmount = amount, Currency = local.Currency ?? plan.CurrencyCode.ToUpperInvariant(),
            TaxRemittedByProcessor = true, IsSubscriptionPayment = true,
            PaidAt = paidAt ?? DateTime.UtcNow, RecordedAt = DateTime.UtcNow, Status = PaymentRecordStatus.Confirmed
        };
        if (await GrantPaidPeriod(local.User.ExternalId, plan, local.ExternalId, reference, payment) && first)
            await RecordDoubleSubscription(local.User.ExternalId, plan, PlayProvider, local.ExternalId, local.PreviousSubscriptionId);
    }

    private async Task RefundGooglePlayOrder(UserSubscription local, string order)
    {
        var payment = await context.PaymentRecords.FirstOrDefaultAsync(p => p.Provider == PlayProvider
            && p.ExternalOrderId == order && p.ExternalTransactionId == local.ExternalId);
        await ApplyInvoiceRefund(local, order, "renewal", payment?.PaidAt ?? DateTime.UtcNow, fullRefund: true);
        if (payment == null || payment.Status == PaymentRecordStatus.Refunded)
            return;
        payment.Status = PaymentRecordStatus.Refunded;
        payment.RefundedAmount = payment.GrossAmount;
        payment.RefundedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();
    }

    private async Task<string> LatestGrantedGooglePlayOrder(UserSubscription local)
    {
        var reference = await context.FiniteTransactions.Where(t => t.SubscriptionId == local.ExternalId
                && t.User.Id == local.User.Id && t.Amount < 0 && t.Reference.StartsWith(PlayOrderPrefix))
            .OrderByDescending(t => t.Id).Select(t => t.Reference).FirstOrDefaultAsync();
        return reference?[PlayOrderPrefix.Length..];
    }

    private async Task AlignGooglePlayAccess(UserSubscription local, DateTime? expiry, bool granted, bool wasSuspended)
    {
        var access = await GooglePlayOwnership(local);
        if (access == null)
            return;
        if (local.Status is "unpaid" or "paused" or "expired")
            access.Expires = Earlier(access.Expires, expiry is DateTime end ? Earlier(end, DateTime.UtcNow) : DateTime.UtcNow);
        else if (expiry is DateTime end)
            access.Expires = granted && (local.Status == "past_due" || wasSuspended) ? end : Earlier(access.Expires, end.AddHours(3));
    }

    private Task<OwnerShip> GooglePlayOwnership(UserSubscription local) =>
        context.OwnerShips.SingleOrDefaultAsync(o => o.UserId == local.User.Id && o.SubscriptionId == local.ExternalId);

    private static DateTime Earlier(DateTime a, DateTime b) => a < b ? a : b;

    private async Task EndGooglePlayAccess(UserSubscription local)
    {
        var now = DateTime.UtcNow;
        local.Status = "expired";
        local.EndsAt = Earlier(local.EndsAt ?? now, now);
        local.UpdatedAt = now;
        var access = await GooglePlayOwnership(local);
        if (access != null)
            access.Expires = Earlier(access.Expires, now);
        await context.SaveChangesAsync();
    }

    private Task<bool> WasGooglePlayReplaced(UserSubscription local) =>
        local.Status != "expired" ? Task.FromResult(false) : context.Subscriptions.AnyAsync(s =>
            s.ProviderSlug == PlayProvider && s.PreviousSubscriptionId == local.ExternalId
            && context.OwnerShips.Any(o => o.SubscriptionId == s.ExternalId && o.UserId == s.User.Id));

    private async Task CloseLinkedGooglePlay(UserSubscription local)
    {
        if (local.PreviousSubscriptionId == null || local.PreviousSubscriptionId == local.ExternalId)
            return;
        await WithSubscriptionLock(local.PreviousSubscriptionId, async () =>
        {
            var previous = await FindProviderSubscription(PlayProvider, local.PreviousSubscriptionId);
            if (previous != null && previous.Status != "expired")
                await EndGooglePlayAccess(previous);
            return true;
        });
    }

    private async Task RecordDoubleSubscription(string userId, TopUpProduct plan, string provider, string id, string linkedId = null)
    {
        var blocking = await ActiveRenewingInLine(context, planLines, userId, plan,
            provider == PlayProvider ? null : PlayProvider, id, linkedId);
        if (blocking == null)
            return;
        PaymentMetrics.GooglePlayDoubleSubscriptions.Inc();
        logger.LogWarning("Paid {Provider} subscription {SubscriptionId} overlaps renewing {BlockingProvider} subscription {BlockingId}",
            provider, id, SubscriptionProviders.Normalize(blocking.ProviderSlug), blocking.ExternalId);
    }

    private static string GooglePlayState(string state, bool renews) => state switch
    {
        "SUBSCRIPTION_STATE_ACTIVE" => renews ? "active" : "cancelled",
        "SUBSCRIPTION_STATE_CANCELED" => "cancelled",
        "SUBSCRIPTION_STATE_IN_GRACE_PERIOD" => "past_due",
        "SUBSCRIPTION_STATE_ON_HOLD" => "unpaid",
        "SUBSCRIPTION_STATE_PAUSED" => "paused",
        "SUBSCRIPTION_STATE_PENDING" => "pending",
        "SUBSCRIPTION_STATE_EXPIRED" or "SUBSCRIPTION_STATE_PENDING_PURCHASE_CANCELED" => "expired",
        _ => null
    };

    private static GooglePlaySubscriptionResult PlayRejected(string error, string status = null) => new(false, status, error);

    private sealed class GooglePlayProvider(SubscriptionService service) : ISubscriptionProvider
    {
        public string Slug => PlayProvider;
        public bool CanResume(UserSubscription subscription) => false;
        public bool CanChangePlan(UserSubscription subscription) => false;
        public bool OffersPlan(TopUpProduct plan) => false;
        public Task CancelAtPeriodEnd(UserSubscription subscription) => throw new ApiException(GooglePlayManaged);
        public Task Resume(UserSubscription subscription) => throw new ApiException(GooglePlayManaged);
        public Task<SubscriptionChangeResult> ChangePlan(UserSubscription subscription, TopUpProduct target) => throw new ApiException(GooglePlayManaged);
        public Task<RefundResponse> RefundInvoice(UserSubscription subscription, SubscriptionInvoice invoice, int? amountCents) =>
            throw new ApiException(GooglePlayManaged);

        public Task<IReadOnlyList<SubscriptionInvoice>> GetInvoices(UserSubscription subscription) => service.GooglePlayInvoices(subscription);
    }

    private async Task<IReadOnlyList<SubscriptionInvoice>> GooglePlayInvoices(UserSubscription subscription)
    {
        var grants = await context.FiniteTransactions.Where(t => t.SubscriptionId == subscription.ExternalId
                && t.User.Id == subscription.User.Id && t.Amount < 0 && t.Reference.StartsWith(PlayOrderPrefix))
            .OrderByDescending(t => t.Id).Select(t => new { t.Reference, t.Timestamp }).ToListAsync();
        var payments = await context.PaymentRecords.Where(p => p.Provider == PlayProvider && p.ExternalTransactionId == subscription.ExternalId)
            .ToDictionaryAsync(p => p.ExternalOrderId);
        var refunded = (await context.RefundedSubscriptionInvoices.Where(r => r.SubscriptionId == subscription.ExternalId)
            .Select(r => r.InvoiceId).ToListAsync()).ToHashSet();
        return grants.Select(g =>
        {
            var order = g.Reference[PlayOrderPrefix.Length..];
            var payment = payments.GetValueOrDefault(order);
            var wasRefunded = refunded.Contains(order);
            var total = payment == null ? 0 : (int)StripeAmounts.ToMinorUnits(payment.GrossAmount, payment.Currency);
            return new SubscriptionInvoice
            {
                Id = order, SubscriptionId = subscription.Id, BillingReason = "renewal",
                CreatedAt = payment?.PaidAt ?? g.Timestamp, UpdatedAt = payment?.RefundedAt ?? g.Timestamp,
                Currency = payment?.Currency ?? subscription.Currency, Total = total, Subtotal = total,
                Status = wasRefunded ? "refunded" : "paid", Refunded = wasRefunded,
                RefundedAmount = wasRefunded ? total : 0
            };
        }).ToList();
    }
}
