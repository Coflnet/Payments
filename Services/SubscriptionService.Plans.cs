using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Models.LemonSqueezy;
using Microsoft.EntityFrameworkCore;

namespace Coflnet.Payments.Services;

public partial class SubscriptionService
{
    internal static string SubscriptionInterval(long seconds) => seconds switch
    {
        >= 2332800 and <= 2678400 => "week_4",
        >= 7516800 and <= 8035200 => "month_3",
        31536000 => "year_1",
        _ => null
    };

    private static int TierRank(Product product) => product.SlotTier switch
    {
        "premium_plus" => 3, "premium" => 2, "starter_premium" => 1,
        _ => product.Groups?.Any(g => g.Slug == "premium_plus") == true ? 3
            : product.Groups?.Any(g => g.Slug == "premium") == true ? 2
            : product.Groups?.Any(g => g.Slug == "starter_premium") == true ? 1 : 0
    };

    private static bool CanChangePlan(Product current, TopUpProduct target) =>
        target.ProviderSlug == "lemonsqueezy" && target.Type.HasFlag(Product.ProductType.SERVICE)
        && !target.Type.HasFlag(Product.ProductType.DISABLED) && TierRank(current) > 0
        && TierRank(target) != TierRank(current) && current.SlotCount == target.SlotCount
        && SubscriptionInterval(current.OwnershipSeconds) is string interval
        && interval == SubscriptionInterval(target.OwnershipSeconds);

    private Task<UserSubscription> OwnedSubscription(string userId, string externalId) => context.Subscriptions
        .Include(s => s.User).Include(s => s.Product).ThenInclude(p => p.Groups)
        .Where(s => s.User.ExternalId == userId && s.ExternalId == externalId)
        .OrderByDescending(s => s.UpdatedAt).FirstOrDefaultAsync();

    public async Task<List<SubscriptionPlan>> GetAvailablePlans(string userId, string externalId)
    {
        var subscription = await OwnedSubscription(userId, externalId)
            ?? throw new ApiException("Subscription not found");
        if (subscription.Status != "active" || subscription.EndsAt != null)
            return [];
        var slugs = lemonSqueezyService.SubscriptionVariants.Keys.ToArray();
        var products = await context.TopUpProducts.Include(p => p.Groups).Where(p => slugs.Contains(p.Slug)).ToListAsync();
        var access = await AccessProduct(subscription);
        return products.Where(p => CanChangePlan(subscription.Product, p))
            .OrderBy(p => p.Price).Select(p => new SubscriptionPlan(p.Slug, p.Title, p.Price,
                p.CurrencyCode, p.OwnershipSeconds, p.SlotCount, TierRank(p) > TierRank(access))).ToList();
    }

    public Task<SubscriptionChangeResult> ChangePlan(string userId, string externalId, string targetProductSlug) =>
        WithSubscriptionLock(externalId, () => ChangePlanLocked(userId, externalId, targetProductSlug));

    private async Task<SubscriptionChangeResult> ChangePlanLocked(string userId, string externalId, string targetProductSlug)
    {
        var subscription = await OwnedSubscription(userId, externalId)
            ?? throw new ApiException("Subscription not found");
        if (subscription.Status != "active" || subscription.EndsAt != null)
            throw new ApiException("Only an active, uncancelled subscription can change plans.");
        if (subscription.Product.Slug == targetProductSlug)
            return new("completed");
        var target = await context.TopUpProducts.Include(p => p.Groups).SingleOrDefaultAsync(p => p.Slug == targetProductSlug);
        if (target == null || !CanChangePlan(subscription.Product, target)
            || !lemonSqueezyService.SubscriptionVariants.TryGetValue(targetProductSlug, out var variantId))
            throw new ApiException("This subscription plan change is unavailable.");
        await PrepareOwnership(subscription);
        await lemonSqueezyService.ValidateSubscriptionVariant(target, variantId);
        var remote = await lemonSqueezyService.GetSubscription(externalId);
        if (remote.Id != externalId || remote.Attributes.Status != "active" || remote.Attributes.EndsAt != null
            || remote.Attributes.FirstSubscriptionItem?.Quantity > 1)
            throw new ApiException("The subscription is not available for a plan change. Reload its status first.");
        // Recover a change accepted by the provider before a lost response or interrupted DB write.
        if (subscription.ProviderVariantId.HasValue && subscription.ProviderVariantId != remote.Attributes.VariantId
            && remote.Attributes.VariantId == variantId)
            return new(await SynchronizePlan(subscription, remote) ? "completed" : "pending");
        subscription.ProviderVariantId = remote.Attributes.VariantId;
        await context.SaveChangesAsync();
        var before = await lemonSqueezyService.GetSubscriptionInvoicesAsync(externalId);
        var upgrade = TierRank(target) > TierRank(await AccessProduct(subscription));
        var updated = await lemonSqueezyService.ChangeSubscription(externalId, variantId, invoiceImmediately: upgrade);
        if (updated.Attributes.PaymentProcessor == "paypal")
        {
            var redirect = updated.Attributes.Urls?.CustomerPortalUpdateSubscription;
            if (!Uri.TryCreate(redirect, UriKind.Absolute, out var uri) || uri.Scheme != "https")
                throw new ApiException("Lemon Squeezy did not return a subscription update link.");
            return new("redirect", redirect);
        }
        if (updated.Attributes.VariantId != variantId || updated.Attributes.Status != "active")
            return new("pending");
        var invoices = await lemonSqueezyService.GetSubscriptionInvoicesAsync(externalId);
        var invoice = invoices.Where(i => i.BillingReason == "updated" && !before.Any(b => b.Id == i.Id))
            .OrderByDescending(i => i.CreatedAt).FirstOrDefault();
        if (upgrade && invoice?.Status is not ("paid" or "partial_refund"))
            return new("pending");
        return new(await ApplyPlan(subscription, target, updated, invoice) ? "completed" : "pending");
    }

    // Dedicated variant mappings are authoritative after a plan change; checkout custom_data
    // continues to describe the original purchase and must not restore its old product.
    private async Task<bool> SynchronizePlan(UserSubscription subscription, Data remote)
    {
        if (remote.Id != subscription.ExternalId)
            throw new ApiException("Invalid subscription response.");
        // Older checkouts used one variant with custom prices for several plans.
        // Baseline their variant before interpreting a later variant change.
        if (subscription.ProviderVariantId == null)
        {
            subscription.ProviderVariantId = remote.Attributes.VariantId;
            return false;
        }
        if (subscription.ProviderVariantId == remote.Attributes.VariantId)
            return true;
        var slug = lemonSqueezyService.SubscriptionVariants.Where(p => p.Value == remote.Attributes.VariantId)
            .Select(p => p.Key).SingleOrDefault();
        if (slug == null || remote.Attributes.Status is not ("active" or "cancelled"))
            return false;
        if (slug == subscription.Product.Slug)
        {
            subscription.ProviderVariantId = remote.Attributes.VariantId;
            return true;
        }
        var target = await context.TopUpProducts.Include(p => p.Groups).SingleOrDefaultAsync(p => p.Slug == slug);
        if (target == null || !CanChangePlan(subscription.Product, target))
            throw new ApiException("The provider subscription changed to an unsupported plan.");
        var used = await context.SubscriptionPlanChanges.Where(c => c.SubscriptionId == subscription.ExternalId)
            .Select(c => c.InvoiceId).ToListAsync();
        var invoices = await lemonSqueezyService.GetSubscriptionInvoicesAsync(subscription.ExternalId);
        var invoice = invoices.Where(i => i.BillingReason == "updated" && !used.Contains(i.Id)
            && i.CreatedAt >= subscription.RenewsAt.AddSeconds(-subscription.Product.OwnershipSeconds)).OrderByDescending(i => i.CreatedAt).FirstOrDefault();
        if (TierRank(target) > TierRank(await AccessProduct(subscription)) && invoice?.Status is not ("paid" or "partial_refund"))
            return false;
        await PrepareOwnership(subscription);
        return await ApplyPlan(subscription, target, remote, invoice);
    }

    private async Task<bool> ApplyPlan(UserSubscription subscription, TopUpProduct target, Data remote, SubscriptionInvoice invoice)
    {
        if (remote.Id != subscription.ExternalId || remote.Attributes.RenewsAt == null)
            throw new ApiException("Invalid subscription update response.");
        if (!await lemonSqueezyService.ValidateSubscriptionPrice(remote, target))
            return false;
        await transactionService.WithTransactionAsync(async (_, _) =>
        {
            var access = await AccessProduct(subscription);
            if (TierRank(target) > TierRank(access))
            {
                if (invoice != null)
                    context.SubscriptionPlanChanges.Add(new SubscriptionPlanChange
                    {
                        SubscriptionId = subscription.ExternalId, PreviousProduct = access, Product = target,
                        ChangedAt = remote.Attributes.UpdatedAt, PeriodEnd = await AccessExpiry(subscription) ?? subscription.RenewsAt, InvoiceId = invoice.Id
                    });
                await SetAccessProduct(subscription, target);
            }
            // A downgrade changes the next bill; the current paid tier lasts until renewal.

            subscription.Product = target;
            subscription.ProviderVariantId = remote.Attributes.VariantId;
            // Billing dates/status may change without extending the paid entitlement.
            subscription.RenewsAt = remote.Attributes.RenewsAt.Value;
            subscription.Status = remote.Attributes.Status;
            subscription.UpdatedAt = remote.Attributes.UpdatedAt;
            await context.SaveChangesAsync();
        });
        return true;
    }

    private async Task<Product> AccessProduct(UserSubscription subscription)
    {
        if (subscription.Product.SlotCount == 0)
            return (await context.OwnerShips.Include(o => o.Product).ThenInclude(p => p.Groups)
                .SingleOrDefaultAsync(o => o.UserId == subscription.User.Id && o.SubscriptionId == subscription.ExternalId))?.Product ?? subscription.Product;
        var tier = await context.TierSlots.Where(s => s.UserId == subscription.User.Id && s.SubscriptionId == subscription.ExternalId)
            .Select(s => s.Tier).FirstOrDefaultAsync();
        if (tier == null || tier == subscription.Product.SlotTier)
            return subscription.Product;
        return await context.TopUpProducts.Include(p => p.Groups).FirstAsync(p => p.ProviderSlug == "lemonsqueezy"
            && p.SlotTier == tier && p.SlotCount == subscription.Product.SlotCount
            && p.OwnershipSeconds == subscription.Product.OwnershipSeconds);
    }

    private Task<DateTime?> AccessExpiry(UserSubscription subscription) => subscription.Product.SlotCount > 0
        ? context.TierSlots.Where(s => s.UserId == subscription.User.Id && s.SubscriptionId == subscription.ExternalId)
            .Select(s => (DateTime?)s.Expires).MaxAsync()
        : context.OwnerShips.Where(o => o.UserId == subscription.User.Id && o.SubscriptionId == subscription.ExternalId)
            .Select(o => (DateTime?)o.Expires).SingleOrDefaultAsync();

    private async Task SetAccessProduct(UserSubscription subscription, Product product)
    {
        if (product.SlotCount > 0)
        {
            var slots = await context.TierSlots.Where(s => s.UserId == subscription.User.Id
                && s.SubscriptionId == subscription.ExternalId).ToListAsync();
            if (slots.Count != product.SlotCount)
                throw new ApiException("Subscription slots could not be loaded. Please contact support.");
            foreach (var slot in slots)
            {
                slot.Tier = product.SlotTier;
                slot.Version++;
            }
        }
        else
        {
            var access = await context.OwnerShips.SingleAsync(o => o.UserId == subscription.User.Id && o.SubscriptionId == subscription.ExternalId);
            access.Product = product;
        }
    }

    private async Task PrepareOwnership(UserSubscription subscription)
    {
        if (subscription.Product.SlotCount > 0)
        {
            if (await context.TierSlots.CountAsync(s => s.UserId == subscription.User.Id
                && s.SubscriptionId == subscription.ExternalId) != subscription.Product.SlotCount)
                throw new ApiException("Subscription slots could not be loaded. Please contact support.");
            return;
        }
        if (await context.OwnerShips.AnyAsync(o => o.UserId == subscription.User.Id && o.SubscriptionId == subscription.ExternalId))
            return;
        // Separate the most recent legacy paid period, retaining time bought independently.
        var prefix = subscription.ExternalId;
        var purchase = await context.FiniteTransactions.Where(t => t.User.Id == subscription.User.Id
            && t.ProductId == subscription.Product.Id && t.Amount < 0 && t.SubscriptionId == null
            && t.Reference.StartsWith(prefix) && !t.Reference.EndsWith("-topup"))
            .OrderByDescending(t => t.Timestamp).FirstOrDefaultAsync();
        if (purchase == null || purchase.Reference.Length != prefix.Length + 10
            || !DateTime.TryParseExact(purchase.Reference[prefix.Length..], "yyyy-MM-dd", null,
                System.Globalization.DateTimeStyles.None, out _)
            || await context.FiniteTransactions.AnyAsync(t => t.Reference == "revert transaction " + purchase.Id))
            throw new ApiException("The legacy subscription's paid period needs reconciliation. Please contact support before changing plans.");
        await transactionService.WithTransactionAsync(async (_, _) =>
        {
            var groups = subscription.Product.Groups.Select(g => g.Slug).ToArray();
            var access = await context.OwnerShips.Include(o => o.Product).Where(o => o.UserId == subscription.User.Id
                && o.SubscriptionId == null && groups.Contains(o.Product.Slug)).ToListAsync();
            foreach (var ownership in access)
                ownership.Expires = ownership.Expires.AddSeconds(-subscription.Product.OwnershipSeconds);
            purchase.SubscriptionId = subscription.ExternalId;
            var grace = SubscriptionInterval(subscription.Product.OwnershipSeconds) == "week_4"
                ? Math.Max(0, subscription.Product.OwnershipSeconds - 2419200) : 0;
            context.OwnerShips.Add(new OwnerShip { User = subscription.User, Product = subscription.Product,
                SubscriptionId = subscription.ExternalId, Expires = (subscription.EndsAt ?? subscription.RenewsAt).AddSeconds(grace) });
            await context.SaveChangesAsync();
        });
    }

    // A database lease serializes provider mutations with webhook reconciliation across replicas.
    // Failed callbacks return an error so Lemon Squeezy retries after the in-flight operation.
    private async Task<T> WithSubscriptionLock<T>(string externalId, Func<Task<T>> action)
    {
        var now = DateTime.UtcNow;
        var until = now.AddMinutes(5);
        var subscriptions = context.Subscriptions.Where(s => s.ExternalId == externalId);
        var exists = await subscriptions.AnyAsync();
        if (exists && await subscriptions.Where(s => s.ChangeLockUntil == null || s.ChangeLockUntil < now)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.ChangeLockUntil, until)) == 0)
            throw new ApiException("A subscription change is already being processed. Please retry shortly.");
        try { return await action(); }
        finally
        {
            if (exists)
                await subscriptions.Where(s => s.ChangeLockUntil == until)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.ChangeLockUntil, (DateTime?)null));
        }
    }
}
