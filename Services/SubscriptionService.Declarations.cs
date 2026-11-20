using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Coflnet.Payments.Services;

/// <summary>
/// The confirmation of a subscription purchase repeats the buyer's early performance declaration (lawyer decision). The
/// declaration of a checkout is linked to its payment request (<see cref="SubscriptionDeclarationService"/>): Stripe names that
/// request in the subscription metadata, a PayPal checkout request has the PayPal subscription id as session id. An extension
/// term's declaration has its own purchase reference (<see cref="SubscriptionDeclarationService.ExtensionReference"/>).
/// </summary>
public partial class SubscriptionService
{
    /// <summary>
    /// Stripe: the subscription's first payment (also the first one after a trial) and the first payment of an extension term carry
    /// their declaration; every payment the locale of the latest one
    /// </summary>
    private async Task<SubscriptionPaymentOutcome> WithStripeDeclaration(SubscriptionPaymentOutcome outcome,
        StripeSubscriptionSnapshot snapshot, StripeInvoiceInfo invoice, bool periodPayment, bool extensionInvoice)
    {
        var subscription = outcome.Subscription;
        var checkout = await DeclarationOfRequest(subscription, StripeCheckoutRequestId(snapshot));
        var extension = await ExtensionDeclaration(subscription);
        if (periodPayment && (invoice.NormalizedBillingReason == "initial" || outcome.FollowsTrial))
            return WithDeclaration(outcome, checkout, checkout, true);
        if (periodPayment && extensionInvoice)
            return WithDeclaration(outcome with { IsExtension = true }, extension, checkout, true);
        return WithDeclaration(outcome, null, extension ?? checkout, false);
    }

    /// <summary>
    /// PayPal: the first sale of a subscription carries the declaration of its checkout; that of a continuation
    /// (<see cref="LinkPayPalContinuation"/>) is the first payment of an extension term, a renewal of the buyer's subscription; that of
    /// a term switch's new subscription is the purchase of the new term
    /// </summary>
    private async Task<SubscriptionPaymentOutcome> WithPayPalDeclaration(SubscriptionPaymentOutcome outcome, bool firstSale)
    {
        var checkout = await DeclarationOfRequest(outcome.Subscription, await PayPalCheckoutRequestId(outcome.Subscription));
        var isExtension = firstSale && outcome.Subscription.PreviousSubscriptionId != null && outcome.TermSwitch == null;
        outcome = outcome with { IsExtension = isExtension, IsRenewal = !firstSale || isExtension };
        return WithDeclaration(outcome, firstSale ? checkout : null, checkout, firstSale);
    }

    private SubscriptionPaymentOutcome WithDeclaration(SubscriptionPaymentOutcome outcome, ServicePerformanceDeclaration given,
        ServicePerformanceDeclaration latest, bool startsDeclaredTerm)
    {
        if (startsDeclaredTerm && given == null)
            logger.LogWarning("{Provider} payment {PaymentId} of subscription {SubscriptionId} starts a term without a stored early performance declaration",
                outcome.Subscription.ProviderSlug, outcome.PaymentId, outcome.Subscription.ExternalId);
        return outcome with { Declaration = startsDeclaredTerm ? given : null, LegalLocale = (given ?? latest)?.Locale };
    }

    private static int? StripeCheckoutRequestId(StripeSubscriptionSnapshot snapshot) =>
        int.TryParse(snapshot.Metadata.GetValueOrDefault(StripeMetadata.PaymentRequestId), out var id) ? id : null;

    private Task<int?> PayPalCheckoutRequestId(UserSubscription subscription) => context.PaymentRequests
        .Where(r => r.Provider == SubscriptionProviders.PayPal && r.SessionId == subscription.ExternalId)
        .OrderByDescending(r => r.Id).Select(r => (int?)r.Id).FirstOrDefaultAsync();

    /// <summary>The declarations are queried by the buyer first (indexed)</summary>
    private async Task<ServicePerformanceDeclaration> DeclarationOfRequest(UserSubscription subscription, int? paymentRequestId)
    {
        if (paymentRequestId == null)
            return null;
        var userId = subscription.User.ExternalId;
        return await context.ServicePerformanceDeclarations.AsNoTracking()
            .Where(d => d.UserId == userId && d.PaymentRequestId == paymentRequestId)
            .OrderByDescending(d => d.Id).FirstOrDefaultAsync();
    }

    /// <summary>The declaration of the latest extension term booked for the subscription (Stripe), null without one</summary>
    private async Task<ServicePerformanceDeclaration> ExtensionDeclaration(UserSubscription subscription)
    {
        if (subscription.ExtensionStartsAt is not System.DateTime start)
            return null;
        var reference = SubscriptionDeclarationService.ExtensionReference(subscription.ExternalId, start);
        var userId = subscription.User.ExternalId;
        return await context.ServicePerformanceDeclarations.AsNoTracking()
            .Where(d => d.UserId == userId && d.PurchaseReference == reference)
            .OrderByDescending(d => d.Id).FirstOrDefaultAsync();
    }

    /// <summary>
    /// The buyer's latest declaration for a subscription without its provider's checkout metadata (renewal reminders): the
    /// extension's, the PayPal checkout's, else (Stripe keeps the checkout request in its metadata only) the newest subscription
    /// checkout declaration of the buyer, preferably for the plan
    /// </summary>
    private async Task<ServicePerformanceDeclaration> LatestDeclaration(UserSubscription subscription)
    {
        var extension = await ExtensionDeclaration(subscription);
        if (extension != null)
            return extension;
        if (SubscriptionProviders.Normalize(subscription.ProviderSlug) == SubscriptionProviders.PayPal)
            return await DeclarationOfRequest(subscription, await PayPalCheckoutRequestId(subscription));
        var userId = subscription.User.ExternalId;
        var productId = subscription.Product.Id;
        return await context.ServicePerformanceDeclarations.AsNoTracking()
            .Where(d => d.UserId == userId && d.PaymentRequestId != null)
            .OrderByDescending(d => d.ProductId == productId).ThenByDescending(d => d.Id).FirstOrDefaultAsync();
    }
}
