using System;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services.Subscriptions;
using Microsoft.Extensions.Logging;

namespace Coflnet.Payments.Services;

/// <summary>
/// A direct Stripe subscription's price changed outside this service (e.g. in the dashboard). Every renewal must charge exactly the
/// agreed <see cref="UserSubscription.PriceCents"/>, otherwise it is refused and marked for a manual refund after the buyer paid.
/// Detected on customer.subscription.updated, weeks before that renewal: a lower price in the same currency and interval is the
/// buyer's gain and becomes the agreed price; anything else (higher, another interval or currency) is logged as an error and counted
/// (payments_foreign_price_changes_total), so it can be set back at Stripe before it is charged. Nothing is changed at Stripe.
/// </summary>
public partial class SubscriptionService
{
    internal const string LowerForeignPrice = "lower";

    /// <summary>
    /// Applies Stripe's state after checking it for a price change this service did not make (before: the state overwrites the
    /// recorded price id). Every place that takes Stripe's state uses it, except right after this service changed the price itself.
    /// </summary>
    private async Task ApplyCheckedStripeState(UserSubscription subscription, StripeSubscriptionSnapshot snapshot)
    {
        await CheckForeignPriceChange(subscription, snapshot);
        ApplyStripeState(subscription, snapshot);
    }

    private async Task CheckForeignPriceChange(UserSubscription subscription, StripeSubscriptionSnapshot snapshot)
    {
        if (!MayHaveForeignPrice(subscription, snapshot) || await LoadPendingTermSwitch(subscription) != null)
            return;
        var kind = ForeignPriceChangeKind(subscription, snapshot);
        if (kind == null)
            return;
        PaymentMetrics.ForeignPriceChanges.WithLabels(kind).Inc();
        if (kind == LowerForeignPrice)
        {
            AcceptLowerForeignPrice(subscription, snapshot);
            return;
        }
        logger.LogError("Stripe subscription {SubscriptionId} of user {UserId} was moved outside this service from price {AgreedPriceId} ({AgreedCents} {AgreedCurrency} every {AgreedWeeks} weeks) to {PriceId} ({Cents} {Currency} every {IntervalCount} {Interval}, {Kind}); its renewal on {RenewsAt} will be refused and need a manual refund unless the price is set back at Stripe",
            subscription.ExternalId, subscription.User?.ExternalId, subscription.ProviderPlanId, subscription.PriceCents, subscription.Currency,
            IntervalWeeks(subscription.Product.OwnershipSeconds), snapshot.PriceId, snapshot.UnitAmount, snapshot.Currency,
            snapshot.IntervalCount, snapshot.Interval, kind, subscription.RenewsAt);
    }

    /// <summary>
    /// A direct subscription billing its recorded plan with another price than the one this service last applied, while nothing this
    /// service does moves its price (a pending update, a 13/52 week first term with its schedule, a booked extension)
    /// </summary>
    private static bool MayHaveForeignPrice(UserSubscription subscription, StripeSubscriptionSnapshot snapshot) =>
        !subscription.ManagedPayments && subscription.PriceCents != null && subscription.ProviderPlanId != null
        && snapshot.PriceId != null && snapshot.PriceId != subscription.ProviderPlanId && snapshot.UnitAmount != null
        && snapshot.Status is "active" or "past_due" && !snapshot.HasPendingUpdate
        && StripePlans.SlugOf(snapshot) == subscription.Product.Slug && !InFirstLongTerm(subscription)
        && !ExtensionBlocksPlanChange(subscription, DateTime.UtcNow);

    /// <summary>"currency", "interval", "higher" or <see cref="LowerForeignPrice"/>; null when the terms did not change</summary>
    private static string ForeignPriceChangeKind(UserSubscription subscription, StripeSubscriptionSnapshot snapshot)
    {
        if (!string.Equals(snapshot.Currency, subscription.Currency, StringComparison.OrdinalIgnoreCase))
            return "currency";
        if (snapshot.IntervalWeeks != IntervalWeeks(subscription.Product.OwnershipSeconds))
            return "interval";
        if (snapshot.UnitAmount > subscription.PriceCents)
            return "higher";
        return snapshot.UnitAmount < subscription.PriceCents ? LowerForeignPrice : null;
    }

    /// <summary>The buyer pays less than agreed: that becomes the agreed price, so the next renewal is granted (saved by the caller)</summary>
    private void AcceptLowerForeignPrice(UserSubscription subscription, StripeSubscriptionSnapshot snapshot)
    {
        logger.LogWarning("Stripe subscription {SubscriptionId} was moved outside this service to the lower price {PriceId} ({Cents} instead of {AgreedCents} {Currency}); accepting it as the agreed price",
            subscription.ExternalId, snapshot.PriceId, snapshot.UnitAmount, subscription.PriceCents, subscription.Currency);
        subscription.PriceCents = snapshot.UnitAmount;
        subscription.UpdatedAt = DateTime.UtcNow;
    }
}
