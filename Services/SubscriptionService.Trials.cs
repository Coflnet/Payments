using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Coflnet.Payments.Services;

/// <summary>
/// Who the trial confirmation goes to and in what terms (what the provider reported with the trial start)
/// </summary>
/// <param name="StartedAt">When the trial (and the declared service) started</param>
/// <param name="Email">Buyer email if known</param>
/// <param name="Name">Buyer name if known</param>
/// <param name="Currency">Currency the provider bills in (Managed Payments may bill a local one), null: the subscription's</param>
/// <param name="TaxBehavior">Managed Payments: the Stripe price's tax_behavior</param>
public record TrialStart(DateTime StartedAt, string Email = null, string Name = null, string Currency = null, string TaxBehavior = null);

/// <summary>
/// Free trials of direct subscriptions (<see cref="SubscriptionTrials"/> decides them at checkout). Once the provider confirms the trial
/// started (Stripe: the subscription is trialing; PayPal: a trial plan's subscription was activated) the subscription's own access is
/// granted until the trial end, the trial is recorded in <see cref="PaymentContext.TrialUsages"/> (one per buyer and line) and its
/// confirmation is published once (<see cref="UserSubscription.TrialUsedAt"/> marks it published). The first payment at the trial end
/// extends that access like any period; a trial cancelled before it ends just expires (nothing charged, nothing to refund).
/// Term switches, extensions and plan switches are refused with <see cref="TrialActive"/> while the trial runs.
/// </summary>
public partial class SubscriptionService
{
    /// <summary>Error of term switches, extensions and plan switches during a trial</summary>
    internal const string TrialActive = "trial_active";
    /// <summary>The provider's trial end may lie this much after the checkout's trial days (the session or approval took a while)</summary>
    private static readonly TimeSpan TrialEndTolerance = TimeSpan.FromDays(1);

    /// <summary>A direct subscription in its free trial (no payment yet)</summary>
    internal static bool InTrial(UserSubscription subscription, DateTime now) =>
        IsDirectSubscription(subscription) && subscription.TrialEndsAt > now;

    /// <summary>The PaymentProviderTransactionId of a trial confirmation (one per subscription)</summary>
    public static string TrialPaymentId(UserSubscription subscription) => "trial-" + subscription.ExternalId;

    /// <summary>Marks the trial confirmation published, so it is not published again</summary>
    public async Task MarkTrialConfirmed(UserSubscription subscription)
    {
        subscription.TrialUsedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Grants the subscription's own access until <paramref name="requestedEnd"/>, capped at <paramref name="days"/> after
    /// <paramref name="startedAt"/> (plus a day): the provider decides the trial, but never more than the checkout gave. Once per subscription.
    /// </summary>
    private async Task GrantTrialAccess(UserSubscription subscription, DateTime requestedEnd, int days, DateTime startedAt)
    {
        if (subscription.TrialEndsAt != null || subscription.Product.SlotCount > 0)
            return;
        var latest = startedAt.AddDays(days) + TrialEndTolerance;
        var end = requestedEnd < latest ? requestedEnd : latest;
        var userId = subscription.User.Id;
        if (!await context.OwnerShips.AnyAsync(o => o.UserId == userId && o.SubscriptionId == subscription.ExternalId))
            context.OwnerShips.Add(new OwnerShip { User = subscription.User, Product = subscription.Product, SubscriptionId = subscription.ExternalId, Expires = end });
        await RecordTrialUsage(subscription);
        subscription.TrialEndsAt = end;
        subscription.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();
        logger.LogInformation("{Provider} subscription {SubscriptionId} started a {Days} day trial of {Plan} until {TrialEnd}",
            subscription.ProviderSlug, subscription.ExternalId, days, subscription.Product.Slug, end);
    }

    /// <summary>
    /// Records the trial, keyed like Lemon Squeezy's (buyer and plan; eligibility checks the whole line). A second trial of the same plan
    /// (two checkouts opened before either started) is still granted: the provider gives it either way
    /// </summary>
    private async Task RecordTrialUsage(UserSubscription subscription)
    {
        var userId = subscription.User.Id;
        var productId = subscription.Product.Id;
        if (await context.TrialUsages.AnyAsync(t => t.UserId == userId && t.ProductId == productId))
        {
            logger.LogWarning("{Provider} subscription {SubscriptionId} started a trial of {Plan}, which its buyer had used before",
                subscription.ProviderSlug, subscription.ExternalId, subscription.Product.Slug);
            return;
        }
        context.TrialUsages.Add(new TrialUsage
        {
            UserId = userId, ProductId = productId, TrialStartedAt = DateTime.UtcNow, ExternalSubscriptionId = subscription.ExternalId
        });
    }

    /// <summary>
    /// The confirmation of a started trial: amount 0, the trial end and first charge, the checkout's declaration. Null when there is no
    /// trial or its confirmation was published already.
    /// </summary>
    private async Task<SubscriptionPaymentOutcome> TrialConfirmation(UserSubscription subscription, int days, int? checkoutRequestId,
        TrialStart start)
    {
        if (subscription.TrialEndsAt is not DateTime end || subscription.TrialUsedAt != null)
            return null;
        var country = subscription.CheckoutCountry;
        var rate = subscription.ManagedPayments ? 0 : (tax?.RateFor(country) ?? 0) / 100.0;
        var outcome = new SubscriptionPaymentOutcome(subscription, (TopUpProduct)subscription.Product, TrialPaymentId(subscription), 0m,
            (start.Currency ?? subscription.Currency)?.ToUpperInvariant(), country, null, start.Email, start.Name, start.StartedAt,
            EurAmount: 0, TaxRate: rate, PriceTaxBehavior: start.TaxBehavior, Trial: new(days, end, subscription.PriceCents));
        var declaration = await DeclarationOfRequest(subscription, checkoutRequestId);
        return WithDeclaration(outcome, declaration, declaration, true);
    }

    /// <summary>
    /// Whether the payment granted under <paramref name="reference"/> is (or will be, when nothing was granted yet) the subscription's first
    /// granted payment. Queried by user first (FiniteTransactions is indexed by user).
    /// </summary>
    private async Task<bool> IsFirstGrantedPayment(UserSubscription subscription, string reference)
    {
        var first = await context.FiniteTransactions
            .Where(t => t.User.Id == subscription.User.Id && t.SubscriptionId == subscription.ExternalId && t.Amount < 0)
            .OrderBy(t => t.Id).Select(t => t.Reference).FirstOrDefaultAsync();
        return first == null || first == reference;
    }

    /// <summary>The first payment after the subscription's trial</summary>
    private async Task<bool> FollowsTrial(UserSubscription subscription, string reference) =>
        subscription.TrialEndsAt != null && await IsFirstGrantedPayment(subscription, reference);

    // Stripe

    private static int? StripeTrialDays(StripeSubscriptionSnapshot snapshot) =>
        int.TryParse(snapshot.Metadata.GetValueOrDefault(SubscriptionTrials.MetadataKey), out var days) && days > 0 ? days : null;

    /// <summary>Grants a trial Stripe started for a checkout that gave one (its metadata names the days)</summary>
    private async Task GrantStripeTrial(UserSubscription subscription, StripeSubscriptionSnapshot snapshot)
    {
        if (snapshot.Status != "trialing" || snapshot.TrialEnd is not DateTime end || StripeTrialDays(snapshot) is not int days)
            return;
        await GrantTrialAccess(subscription, end, days, snapshot.Created);
    }

    private Task<SubscriptionPaymentOutcome> StripeTrialConfirmation(UserSubscription subscription, StripeSubscriptionSnapshot snapshot,
        TrialStart start) =>
        TrialConfirmation(subscription, StripeTrialDays(snapshot) ?? 0, StripeCheckoutRequestId(snapshot),
            start with { TaxBehavior = snapshot.TaxBehavior });

    /// <summary>The zero invoice Stripe creates when a trial starts (billing reason subscription_create)</summary>
    private static bool IsTrialStartInvoice(StripeInvoiceInfo invoice) =>
        invoice.NormalizedBillingReason == "initial" && invoice.Total == 0 && invoice.AmountPaid == 0;

    /// <summary>
    /// The trial's zero invoice pays nothing: it confirms the trial (once) and is never granted or marked for a refund. One without a
    /// trial of ours (e.g. a trial added in the dashboard) is logged and ignored.
    /// </summary>
    private async Task<SubscriptionPaymentOutcome> StripeTrialInvoice(UserSubscription subscription, StripeSubscriptionSnapshot snapshot,
        StripeInvoiceInfo invoice)
    {
        if (subscription.TrialEndsAt == null)
        {
            logger.LogWarning("Stripe invoice {InvoiceId} of {SubscriptionId} charged nothing but the subscription has no trial of this service; ignoring it",
                invoice.Id, subscription.ExternalId);
            return null;
        }
        return await StripeTrialConfirmation(subscription, snapshot,
            new TrialStart(invoice.Created, invoice.CustomerEmail, invoice.CustomerName, invoice.Currency));
    }
}
