using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Coflnet.Payments.Services;

/// <summary>
/// Stripe Billing subscriptions (direct and Managed Payments). Stripe does not guarantee webhook order,
/// so every handler re-reads the subscription from Stripe and creates the local row when it is missing.
/// Paid periods are granted only for paid invoices, once per invoice (reference stripe-invoice-{id}).
/// </summary>
public partial class SubscriptionService
{
    /// <summary>Metadata keys written on the checkout session and the Stripe subscription</summary>
    public static class StripeMetadata
    {
        public const string UserId = "userId";
        public const string ProductSlug = "productSlug";
        public const string ProductId = "productId";
        public const string PriceCents = "priceCents";
        public const string Mode = "mode";
        public const string Country = "country";
        public const string DiscountCode = "discountCode";
        public const string CreatorCode = "creatorCode";
        public const string PaymentRequestId = "paymentRequestId";
    }

    private IStripeSubscriptionGateway StripeApi =>
        stripe ?? throw new ApiException("Stripe subscriptions are not available.");

    /// <summary>
    /// checkout.session.completed in subscription mode: records the subscription; the payment is granted by invoice.paid.
    /// </summary>
    public async Task StripeCheckoutCompleted(Stripe.Checkout.Session session)
    {
        if (string.IsNullOrEmpty(session.SubscriptionId))
            throw new ApiException("Subscription checkout without subscription");
        var userId = session.Metadata?.GetValueOrDefault(StripeMetadata.UserId);
        if (userId == null || userId != session.ClientReferenceId)
            throw new ApiException("Subscription checkout owner does not match its metadata");
        var recorded = await WithSubscriptionLock(session.SubscriptionId, async () =>
        {
            var snapshot = await StripeApi.GetSubscription(session.SubscriptionId);
            var subscription = await EnsureStripeSubscription(snapshot);
            if (subscription == null)
                return false;
            ApplyStripeState(subscription, snapshot);
            await context.SaveChangesAsync();
            return true;
        });
        // Delayed payment methods complete the checkout unpaid; invoice.paid grants once the money arrived.
        if (recorded && session.PaymentStatus == "paid")
            await MarkCheckoutPaid(SubscriptionProviders.Stripe, session.Id);
    }

    /// <summary>
    /// customer.subscription.updated/deleted and invoice.payment_failed: synchronizes status, EndsAt and RenewsAt.
    /// </summary>
    public Task StripeSubscriptionChanged(string subscriptionId) => WithSubscriptionLock(subscriptionId, async () =>
    {
        var snapshot = await StripeApi.GetSubscription(subscriptionId);
        var subscription = await EnsureStripeSubscription(snapshot);
        if (subscription == null)
            return false;
        ApplyStripeState(subscription, snapshot);
        await context.SaveChangesAsync();
        return true;
    });

    /// <summary>
    /// invoice.paid: grants the paid period exactly once. Returns the payment to record (also when a redelivery
    /// finds it already granted, so recording can be retried); null when nothing was paid for.
    /// A paid invoice that can not be granted is marked for a manual refund, never dropped.
    /// </summary>
    public Task<SubscriptionPaymentOutcome> StripeInvoicePaid(StripeInvoiceInfo invoice)
    {
        if (string.IsNullOrEmpty(invoice?.SubscriptionId) || invoice.Status != "paid")
            return Task.FromResult<SubscriptionPaymentOutcome>(null);
        return WithSubscriptionLock(invoice.SubscriptionId, () => StripeInvoicePaidLocked(invoice));
    }

    private async Task<SubscriptionPaymentOutcome> StripeInvoicePaidLocked(StripeInvoiceInfo invoice)
    {
        if (await context.RefundedSubscriptionInvoices.AnyAsync(i => i.InvoiceId == invoice.Id))
            return null;
        var snapshot = await StripeApi.GetSubscription(invoice.SubscriptionId);
        var subscription = await EnsureStripeSubscription(snapshot);
        if (subscription == null)
            return await RejectStripeInvoice(null, invoice, "the subscription was not sold by this service");
        ApplyStripeState(subscription, snapshot);
        await context.SaveChangesAsync();
        return invoice.NormalizedBillingReason switch
        {
            "initial" or "renewal" => await GrantStripeInvoice(subscription, snapshot, invoice),
            "updated" => await RecordStripeProration(subscription, snapshot, invoice),
            _ => await RejectStripeInvoice(subscription, invoice, $"unexpected billing reason {invoice.BillingReason}")
        };
    }

    private async Task<SubscriptionPaymentOutcome> GrantStripeInvoice(UserSubscription subscription,
        StripeSubscriptionSnapshot snapshot, StripeInvoiceInfo invoice)
    {
        var reference = SubscriptionProviders.GrantReference(SubscriptionProviders.Stripe, invoice.Id);
        if (await IsGranted(subscription.User.ExternalId, reference))
        {
            var granted = StripeOutcome(subscription, snapshot, invoice, subscription.PriceCents / 100m);
            await AfterStripeGrant(subscription, snapshot, invoice);
            return granted;
        }
        if (!await ReconcileStripePlan(subscription, snapshot))
            return await RejectStripeInvoice(subscription, invoice,
                $"Stripe bills plan {StripePlans.SlugOf(snapshot)}, which is no valid change from {subscription.Product.Slug}");
        var rejection = StripeAmountRejection(subscription, invoice);
        if (rejection != null)
            return await RejectStripeInvoice(subscription, invoice, rejection);
        if (invoice.NormalizedBillingReason == "initial" && !await AcceptsDirectBuyer(subscription, invoice))
            return null;
        await GrantPaidPeriod(subscription.User.ExternalId, (TopUpProduct)subscription.Product, subscription.ExternalId, reference);
        var outcome = StripeOutcome(subscription, snapshot, invoice, subscription.PriceCents / 100m);
        subscription.PaymentAmount = (invoice.AmountPaid / 100m).ToString("0.00", CultureInfo.InvariantCulture) + " " + invoice.Currency?.ToUpperInvariant();
        await AfterStripeGrant(subscription, snapshot, invoice);
        return outcome;
    }

    /// <summary>
    /// Idempotent follow-up of a granted invoice; a redelivery retries it if it failed before.
    /// </summary>
    private async Task AfterStripeGrant(UserSubscription subscription, StripeSubscriptionSnapshot snapshot, StripeInvoiceInfo invoice)
    {
        if (invoice.NormalizedBillingReason == "initial")
            await EndFirstPaymentDiscount(subscription, snapshot);
        await context.SaveChangesAsync();
    }

    private SubscriptionPaymentOutcome StripeOutcome(UserSubscription subscription, StripeSubscriptionSnapshot snapshot,
        StripeInvoiceInfo invoice, decimal? eurAmount) =>
        new(subscription, (TopUpProduct)subscription.Product, invoice.Id, invoice.AmountPaid / 100m,
            invoice.Currency?.ToUpperInvariant(), invoice.CustomerCountry ?? MetadataCountry(snapshot),
            invoice.CustomerPostalCode, invoice.CustomerEmail, invoice.CustomerName, invoice.Created, eurAmount,
            invoice.NormalizedBillingReason == "updated");

    private async Task<SubscriptionPaymentOutcome> RejectStripeInvoice(UserSubscription subscription, StripeInvoiceInfo invoice, string reason)
    {
        await MarkForManualRefund(SubscriptionProviders.Stripe, invoice.Id, subscription, invoice.AmountPaid / 100m,
            $"Stripe invoice {invoice.Id} of subscription {invoice.SubscriptionId}: {reason}");
        return null;
    }

    /// <summary>
    /// Stripe is authoritative for the plan it bills. A plan switch whose local update was lost (interrupted request,
    /// upgrade paid later) is applied here, as long as it is a switch this service offers.
    /// </summary>
    private async Task<bool> ReconcileStripePlan(UserSubscription subscription, StripeSubscriptionSnapshot snapshot)
    {
        var slug = StripePlans.SlugOf(snapshot);
        if (slug == null || slug == subscription.Product.Slug)
            return true;
        var target = await PlanBySlug(slug);
        if (target == null || !CanChangePlan(subscription.Product, target))
            return false;
        logger.LogWarning("Stripe subscription {SubscriptionId} bills {Billed} while {Local} was recorded; applying Stripe's plan",
            subscription.ExternalId, slug, subscription.Product.Slug);
        await PrepareOwnership(subscription);
        await ApplyPlanLocally(subscription, target, null, DateTime.UtcNow, s => ApplyStripePrice(s, snapshot));
        return true;
    }

    /// <summary>
    /// Direct invoices must charge exactly the price this service set. Managed Payments may charge in the
    /// buyer's local currency (Adaptive Pricing), so only the plan is verified for those.
    /// </summary>
    private static string StripeAmountRejection(UserSubscription subscription, StripeInvoiceInfo invoice)
    {
        if (subscription.ManagedPayments)
            return null;
        if (subscription.PriceCents == null || invoice.Total != subscription.PriceCents)
            return $"total {invoice.Total} differs from the agreed price {subscription.PriceCents}";
        if (!string.Equals(invoice.Currency, subscription.Currency, StringComparison.OrdinalIgnoreCase))
            return $"currency {invoice.Currency} differs from {subscription.Currency}";
        return null;
    }

    /// <summary>
    /// Like one-time payments, a direct sale to a country we can not sell to is not fulfilled:
    /// the subscription ends immediately and the payment is marked for a manual refund.
    /// </summary>
    private async Task<bool> AcceptsDirectBuyer(UserSubscription subscription, StripeInvoiceInfo invoice)
    {
        if (subscription.ManagedPayments)
            return true;
        var country = invoice.CustomerCountry ?? subscription.User.Country;
        if (country != null && SalesRestrictions.DoWeSellTo(country, invoice.CustomerPostalCode))
            return true;
        ApplyStripeState(subscription, await StripeApi.CancelNow(subscription.ExternalId));
        await RejectStripeInvoice(subscription, invoice, $"buyer country {country ?? "UNKNOWN"} is not sold to directly; subscription cancelled");
        return false;
    }

    /// <summary>
    /// A "first payment only" discount code: after the first invoice the price returns to the plan price
    /// (with the creator code), effective from the next renewal.
    /// </summary>
    private async Task EndFirstPaymentDiscount(UserSubscription subscription, StripeSubscriptionSnapshot snapshot)
    {
        var terms = await DiscountTerms(subscription.DiscountCode);
        if (terms?.Duration != DiscountCode.FirstPaymentOnly)
            return;
        var plan = (TopUpProduct)subscription.Product;
        var full = await RecurringPriceCents(subscription, plan);
        if (snapshot.UnitAmount == full)
            return;
        var change = await StripeApi.ChangePrice(snapshot, new StripePriceChangeRequest(full,
            plan.CurrencyCode.ToLowerInvariant(), IntervalWeeks(plan.OwnershipSeconds).Value, false,
            snapshot.TaxBehavior, plan.Slug, plan.Title));
        ApplyStripePrice(subscription, change.Subscription);
    }

    /// <summary>
    /// Proration invoices pay for an upgrade, not another period, but are revenue to record.
    /// The plan change itself is applied by <see cref="ChangePlan"/> when the proration is paid at once;
    /// a delayed payment applies the pending update later and the change is recorded here.
    /// </summary>
    private async Task<SubscriptionPaymentOutcome> RecordStripeProration(UserSubscription subscription,
        StripeSubscriptionSnapshot snapshot, StripeInvoiceInfo invoice)
    {
        var eurAmount = subscription.ManagedPayments ? (decimal?)null : invoice.AmountPaid / 100m;
        if (await context.SubscriptionPlanChanges.AnyAsync(c => c.InvoiceId == invoice.Id))
            return StripeOutcome(subscription, snapshot, invoice, eurAmount);
        if (snapshot.HasPendingUpdate)
            throw new ApiException($"Stripe has not applied the upgrade paid by {invoice.Id} yet; retry later");
        var target = await PlanBySlug(StripePlans.SlugOf(snapshot));
        if (target == null || target.Id != subscription.Product.Id && !CanChangePlan(subscription.Product, target))
            return await RejectStripeInvoice(subscription, invoice, "paid proration without a valid plan change");
        if (target.Id != subscription.Product.Id)
        {
            await PrepareOwnership(subscription);
            await ApplyPlanLocally(subscription, target, invoice.Id, invoice.Created, s => ApplyStripePrice(s, snapshot));
        }
        return StripeOutcome(subscription, snapshot, invoice, eurAmount);
    }

    /// <summary>
    /// The subscription invoice a refunded charge paid, null for one-time payments (credited under the payment intent id).
    /// </summary>
    public async Task<StripeInvoiceInfo> StripeSubscriptionInvoiceForPayment(string paymentIntentId)
    {
        if (string.IsNullOrEmpty(paymentIntentId) || stripe == null
            || await context.FiniteTransactions.AnyAsync(t => t.Reference == paymentIntentId))
            return null;
        var invoice = await stripe.FindInvoiceByPaymentIntent(paymentIntentId);
        return string.IsNullOrEmpty(invoice?.SubscriptionId) ? null : invoice;
    }

    /// <summary>
    /// charge.refunded for a subscription invoice, including refunds Stripe issued on its own (Managed Payments).
    /// </summary>
    public Task StripeInvoiceRefunded(StripeInvoiceInfo invoice, long chargedAmount, long refundedAmount) =>
        WithSubscriptionLock(invoice.SubscriptionId, async () =>
        {
            var subscription = await FindProviderSubscription(SubscriptionProviders.Stripe, invoice.SubscriptionId)
                ?? throw new ApiException("Refunded Stripe subscription is unknown; retry after it was recorded.");
            await ApplyInvoiceRefund(subscription, invoice.Id, invoice.NormalizedBillingReason, invoice.Created,
                chargedAmount > 0 && refundedAmount >= chargedAmount);
            return true;
        });

    /// <summary>
    /// Loads the local row or creates it from the metadata the checkout wrote; null for subscriptions this service did not sell.
    /// </summary>
    private async Task<UserSubscription> EnsureStripeSubscription(StripeSubscriptionSnapshot snapshot)
    {
        var existing = await FindProviderSubscription(SubscriptionProviders.Stripe, snapshot.Id);
        if (existing != null)
            return existing;
        var metadata = snapshot.Metadata;
        var userId = metadata.GetValueOrDefault(StripeMetadata.UserId);
        var product = await PlanBySlug(metadata.GetValueOrDefault(StripeMetadata.ProductSlug));
        if (string.IsNullOrWhiteSpace(userId) || product == null)
        {
            logger.LogWarning("Ignoring Stripe subscription {SubscriptionId}: it was not created by this service", snapshot.Id);
            return null;
        }
        var subscription = new UserSubscription
        {
            User = await userService.GetOrCreate(userId),
            Product = product,
            ProviderSlug = SubscriptionProviders.Stripe,
            ExternalId = snapshot.Id,
            CreatedAt = snapshot.Created,
            ManagedPayments = snapshot.ManagedPayments || metadata.GetValueOrDefault(StripeMetadata.Mode) == "managed",
            PriceCents = long.TryParse(metadata.GetValueOrDefault(StripeMetadata.PriceCents), out var cents) ? cents : null,
            Currency = product.CurrencyCode?.ToUpperInvariant(),
            DiscountCode = metadata.GetValueOrDefault(StripeMetadata.DiscountCode),
            CreatorCode = metadata.GetValueOrDefault(StripeMetadata.CreatorCode)
        };
        ApplyStripeState(subscription, snapshot);
        context.Subscriptions.Add(subscription);
        await context.SaveChangesAsync();
        return subscription;
    }

    private static string MetadataCountry(StripeSubscriptionSnapshot snapshot) =>
        snapshot.Metadata.GetValueOrDefault(StripeMetadata.Country);

    /// <summary>
    /// Maps Stripe's status onto the vocabulary shared with Lemon Squeezy:
    /// a subscription set to cancel at period end is "cancelled" with EndsAt, an ended one "expired".
    /// </summary>
    internal static void ApplyStripeState(UserSubscription subscription, StripeSubscriptionSnapshot snapshot)
    {
        subscription.Status = StripeStatus(snapshot);
        subscription.EndsAt = subscription.Status switch
        {
            "cancelled" => snapshot.CancelAt ?? snapshot.CurrentPeriodEnd,
            "expired" => snapshot.EndedAt ?? DateTime.UtcNow,
            _ => null
        };
        if (snapshot.CurrentPeriodEnd.HasValue)
            subscription.RenewsAt = snapshot.CurrentPeriodEnd.Value;
        subscription.ExternalCustomerId = snapshot.CustomerId ?? subscription.ExternalCustomerId;
        subscription.ManagedPayments |= snapshot.ManagedPayments;
        subscription.ProviderPlanId = snapshot.PriceId ?? subscription.ProviderPlanId;
        subscription.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// After this service changed the price, the Stripe price is the agreed price.
    /// </summary>
    private static void ApplyStripePrice(UserSubscription subscription, StripeSubscriptionSnapshot snapshot)
    {
        ApplyStripeState(subscription, snapshot);
        subscription.PriceCents = snapshot.UnitAmount ?? subscription.PriceCents;
    }

    private static string StripeStatus(StripeSubscriptionSnapshot snapshot) => snapshot.Status switch
    {
        "active" or "trialing" when snapshot.CancelAtPeriodEnd || snapshot.CancelAt != null => "cancelled",
        "active" => "active",
        "trialing" => "on_trial",
        "past_due" => "past_due",
        "unpaid" => "unpaid",
        "paused" => "paused",
        "incomplete" => "pending",
        "canceled" or "incomplete_expired" => "expired",
        _ => snapshot.Status
    };

    /// <summary>
    /// The recurring price for <paramref name="plan"/> with the subscription's creator and discount codes,
    /// as agreed at checkout (codes that expired since still apply to the existing subscription;
    /// "first payment only" codes no longer apply to recurring prices).
    /// </summary>
    private async Task<long> RecurringPriceCents(UserSubscription subscription, TopUpProduct plan)
    {
        var creator = string.IsNullOrWhiteSpace(subscription.CreatorCode) ? null
            : await context.CreatorCodes.FirstOrDefaultAsync(c => c.Code.ToLower() == subscription.CreatorCode.ToLower());
        var discount = await DiscountTerms(subscription.DiscountCode);
        if (discount?.Duration == DiscountCode.FirstPaymentOnly)
            discount = null;
        return SubscriptionPricing.ToCents(SubscriptionPricing.Apply(plan.Price, creator, discount));
    }

    private Task<Models.LemonSqueezy.ValidatedDiscount> DiscountTerms(string code) =>
        new DiscountCodeService(context, Microsoft.Extensions.Logging.Abstractions.NullLogger<DiscountCodeService>.Instance).Terms(code);

    private sealed class StripeProvider(SubscriptionService service) : ISubscriptionProvider
    {
        public string Slug => SubscriptionProviders.Stripe;

        // Resume clears cancel_at_period_end; an end date set elsewhere (cancel_at, e.g. by support) is not undone.
        public bool CanResume(UserSubscription subscription) =>
            subscription.EndsAt == null || subscription.EndsAt == subscription.RenewsAt;

        public bool CanChangePlan(UserSubscription subscription) => true;

        public bool OffersPlan(TopUpProduct plan) => IsSubscriptionPlan(plan);

        public async Task CancelAtPeriodEnd(UserSubscription subscription)
        {
            ApplyStripeState(subscription, await service.StripeApi.SetCancelAtPeriodEnd(subscription.ExternalId, true));
            if (subscription.Status != "cancelled" || subscription.EndsAt == null)
                throw new ApiException("Stripe did not confirm the cancellation.");
        }

        public async Task Resume(UserSubscription subscription)
        {
            ApplyStripeState(subscription, await service.StripeApi.SetCancelAtPeriodEnd(subscription.ExternalId, false));
            if (subscription.Status != "active")
                throw new ApiException("Stripe did not confirm reactivation.");
        }

        public async Task<SubscriptionChangeResult> ChangePlan(UserSubscription subscription, TopUpProduct target)
        {
            var current = await service.StripeApi.GetSubscription(subscription.ExternalId);
            if (current.Status != "active" || current.CancelAtPeriodEnd || current.CancelAt != null || current.HasPendingUpdate
                || current.ItemId == null)
                throw new ApiException("The subscription is not available for a plan change. Reload its status first.");
            await service.PrepareOwnership(subscription);
            var upgrade = TierRank(target) > TierRank(await service.AccessProduct(subscription));
            var priceCents = await service.RecurringPriceCents(subscription, target);
            var change = await service.StripeApi.ChangePrice(current, new StripePriceChangeRequest(priceCents,
                target.CurrencyCode.ToLowerInvariant(), IntervalWeeks(target.OwnershipSeconds).Value, upgrade,
                current.TaxBehavior, target.Slug, target.Title));
            // An unpaid upgrade stays a pending update at Stripe; invoice.paid applies it once the payment succeeds.
            if (upgrade && change.InvoiceStatus != "paid")
                return new("pending");
            await service.ApplyPlanLocally(subscription, target, upgrade ? change.InvoiceId : null, DateTime.UtcNow,
                s => ApplyStripePrice(s, change.Subscription));
            return new("completed");
        }

        public async Task<IReadOnlyList<SubscriptionInvoice>> GetInvoices(UserSubscription subscription)
        {
            var invoices = await service.StripeApi.ListInvoices(subscription.ExternalId);
            var ids = invoices.Select(i => i.Id).ToList();
            var refunded = await service.context.RefundedSubscriptionInvoices.Where(r => ids.Contains(r.InvoiceId))
                .Select(r => r.InvoiceId).ToListAsync();
            foreach (var invoice in invoices.Where(i => refunded.Contains(i.Id)))
            {
                invoice.Refunded = true;
                invoice.Status = "refunded";
                invoice.RefundedAmount = invoice.Total;
            }
            return invoices;
        }

        public Task<RefundResponse> RefundInvoice(UserSubscription subscription, SubscriptionInvoice invoice, int? amountCents) =>
            service.StripeApi.RefundInvoice(invoice.Id, amountCents);
    }
}
