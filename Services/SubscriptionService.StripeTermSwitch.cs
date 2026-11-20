using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Coflnet.Payments.Services;

/// <summary>
/// Stripe term switch (<see cref="SwitchTerm"/>): the subscription item bills the new term's price, the billing cycle restarts now
/// and the term is invoiced at once without proration credit (the paid days left are kept as access instead). The update is a
/// pending update until its invoice is paid, so a payment that needs the buyer's confirmation (SCA) changes nothing until it is
/// confirmed on Stripe's invoice page. The paid invoice (billing reason "updated") is recognised by the recorded switch and granted
/// as a full term of the new plan on top of the current access end; the renewal schedule then moves it to 4 weeks after the term.
/// </summary>
public partial class SubscriptionService
{
    private async Task<SubscriptionTermSwitchResult> SwitchStripeTerm(UserSubscription subscription, TermSwitchQuote quote,
        SubscriptionTermSwitchRequest request)
    {
        var snapshot = await StripeApi.GetSubscription(subscription.ExternalId);
        await ApplyCheckedStripeState(subscription, snapshot);
        await context.SaveChangesAsync();
        await EnsureStripeTermSwitchable(subscription, snapshot);
        var row = await StartTermSwitch(subscription, quote, request.Declaration);
        var change = await ApplyStripeTermSwitch(subscription, snapshot, quote, row);
        if (change == null)
            return TermSwitchResult(subscription, row, "processing", quote.FirstRenewalAt);
        (row.InvoiceId, row.PaymentUrl) = (change.Invoice?.Id, change.PaymentUrl);
        await context.SaveChangesAsync();
        logger.LogInformation("Stripe subscription {SubscriptionId} switches to {Plan} at {Cents} cents (invoice {InvoiceId}, {Status})",
            subscription.ExternalId, quote.Plan.Slug, quote.ChargedCents, change.Invoice?.Id, change.Invoice?.Status);
        if (change.Invoice?.Status != "paid")
            return TermSwitchResult(subscription, row, "payment_required", quote.FirstRenewalAt);
        await TryGrantPaidStripeSwitch(subscription, change.Invoice);
        if (row.Status != TermSwitchStatus.Completed)
            return TermSwitchResult(subscription, row, "processing", quote.FirstRenewalAt);
        return TermSwitchResult(subscription, row, "completed", subscription.RenewsAt, await AccessExpiry(subscription));
    }

    /// <summary>
    /// The buyer paid: the term is granted right away (as invoice.paid would; the webhook then finds it granted and publishes the
    /// confirmation). A failure here is left to the webhook, which Stripe retries.
    /// </summary>
    private async Task TryGrantPaidStripeSwitch(UserSubscription subscription, StripeInvoiceInfo invoice)
    {
        try
        {
            await StripeInvoicePaidLocked(invoice);
        }
        catch (Exception e)
        {
            context.ChangeTracker.Clear();
            logger.LogError(e, "The paid term switch {InvoiceId} of Stripe subscription {SubscriptionId} could not be granted yet; invoice.paid retries it",
                invoice.Id, subscription.ExternalId);
        }
    }

    /// <summary>
    /// Active, not cancelled, no pending update (plan change), billing the plan we recorded, and no schedule someone else planned
    /// changes with (ours is released before the switch)
    /// </summary>
    private async Task EnsureStripeTermSwitchable(UserSubscription subscription, StripeSubscriptionSnapshot snapshot)
    {
        if (snapshot.Status != "active" || snapshot.CancelAtPeriodEnd || snapshot.CancelAt != null || snapshot.HasPendingUpdate
            || snapshot.ItemId == null || StripePlans.SlugOf(snapshot) != subscription.Product.Slug)
            throw new ApiException("subscription_not_switchable");
        if (snapshot.ScheduleId != null && !await IsReleasableRenewalSchedule(subscription, snapshot))
            throw new ApiException("subscription_not_switchable");
    }

    /// <summary>
    /// Releases our renewal schedule and switches at Stripe. A failed call may still have reached Stripe: when Stripe bills the new
    /// term or holds the pending update, the switch stays pending (null: its invoice webhook grants it); otherwise it failed.
    /// </summary>
    private async Task<StripeTermSwitch> ApplyStripeTermSwitch(UserSubscription subscription, StripeSubscriptionSnapshot snapshot,
        TermSwitchQuote quote, SubscriptionTermSwitch row)
    {
        try
        {
            snapshot = await ReleaseStripeRenewal(subscription, snapshot);
            return await StripeApi.SwitchTerm(snapshot, new StripeTermSwitchRequest(quote.ChargedCents,
                quote.Plan.CurrencyCode.ToLowerInvariant(), quote.Weeks, snapshot.TaxBehavior, quote.Plan.Slug, quote.Plan.Title));
        }
        catch (Exception e)
        {
            logger.LogError(e, "Stripe subscription {SubscriptionId} could not be switched to {Plan}; checking whether Stripe applied it",
                subscription.ExternalId, quote.Plan.Slug);
            return await AfterFailedStripeSwitch(subscription, quote, row, e);
        }
    }

    /// <summary>
    /// Stripe applied it (or holds its pending update): pending, its invoice webhook decides. Stripe can not be read: pending for a
    /// short while only (<see cref="UnknownTermSwitchBackoff"/>). Otherwise it failed: the released renewal schedule is attached again
    /// (a 13 week term must keep continuing every 4 weeks) and the buyer is told.
    /// </summary>
    private async Task<StripeTermSwitch> AfterFailedStripeSwitch(UserSubscription subscription, TermSwitchQuote quote,
        SubscriptionTermSwitch row, Exception error)
    {
        var current = await ReadStripeAfterFailure(subscription);
        if (current == null)
        {
            row.ExpiresAt = DateTime.UtcNow + UnknownTermSwitchBackoff;
            await context.SaveChangesAsync();
            return null;
        }
        if (current.HasPendingUpdate || StripePlans.SlugOf(current) == quote.Plan.Slug)
            return null;
        await FailTermSwitch(row);
        await TryScheduleStripeRenewal(subscription, current);
        throw new ApiException(StripeSwitchFailure(error));
    }

    /// <summary>The subscription after a failed switch call (read twice), null when Stripe can not be read</summary>
    private async Task<StripeSubscriptionSnapshot> ReadStripeAfterFailure(UserSubscription subscription)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await StripeApi.GetSubscription(subscription.ExternalId);
            }
            catch (Exception e) when (attempt < 2)
            {
                logger.LogWarning(e, "Stripe subscription {SubscriptionId} could not be read after a failed term switch; retrying", subscription.ExternalId);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Stripe subscription {SubscriptionId} could not be read after a failed term switch; pending for {Minutes} minutes",
                    subscription.ExternalId, UnknownTermSwitchBackoff.TotalMinutes);
                return null;
            }
        }
    }

    /// <summary>
    /// customer.subscription.updated: a switch whose invoice Stripe holds as a pending update is over once the pending update is gone
    /// without Stripe billing the new term (expired unpaid). It no longer blocks and its promotion use is given back; the renewal
    /// schedule a 13 week term released for it is attached again by the caller.
    /// </summary>
    private async Task ExpireUnpaidStripeSwitch(UserSubscription subscription, StripeSubscriptionSnapshot snapshot)
    {
        if (snapshot.HasPendingUpdate)
            return;
        var pending = await LoadPendingTermSwitch(subscription);
        if (pending == null || pending.InvoiceId == null || StripePlans.SlugOf(snapshot) == pending.Product.Slug)
            return;
        logger.LogInformation("The pending switch {SwitchId} of Stripe subscription {SubscriptionId} expired unpaid", pending.Id, subscription.ExternalId);
        await EndPendingTermSwitch(pending, TermSwitchStatus.Expired);
    }

    private static string StripeSwitchFailure(Exception e) => e is Stripe.StripeException { StripeError.Type: "card_error" } stripeError
        ? "The payment failed: " + stripeError.StripeError.Message
        : "The term could not be switched right now. Please try again later.";

    /// <summary>
    /// The recorded switch a paid invoice pays: the one it was invoiced for, or (the response of the switch call was lost) one without
    /// invoice, when the invoice is an update within the time Stripe keeps a pending update after it and Stripe bills the switched term
    /// (also when it stopped blocking meanwhile, e.g. after <see cref="UnknownTermSwitchBackoff"/>)
    /// </summary>
    private async Task<SubscriptionTermSwitch> StripeTermSwitchOf(UserSubscription subscription, StripeSubscriptionSnapshot snapshot,
        StripeInvoiceInfo invoice)
    {
        var candidates = await context.SubscriptionTermSwitches
            .Include(r => r.Product).Include(r => r.PreviousProduct).Include(r => r.RenewalProduct)
            .Where(r => r.Provider == SubscriptionProviders.Stripe && r.SubscriptionId == subscription.ExternalId
                && (r.InvoiceId == invoice.Id || r.InvoiceId == null && r.Status != TermSwitchStatus.Completed))
            .OrderByDescending(r => r.Id).ToListAsync();
        return candidates.FirstOrDefault(r => r.InvoiceId == invoice.Id)
            ?? candidates.FirstOrDefault(r => invoice.NormalizedBillingReason == "updated" && invoice.Created >= r.CreatedAt.AddMinutes(-5)
                && invoice.Created <= r.CreatedAt + TermSwitchPendingFor && StripePlans.SlugOf(snapshot) == r.Product.Slug);
    }

    /// <summary>
    /// Grants the new term once, on top of the current access end (never as a proration, never refused as a full term without a
    /// plan change). Direct invoices must charge exactly the switch's price; an invoice paid although Stripe does not bill the
    /// switched term (its pending update expired) is marked for a manual refund.
    /// </summary>
    private async Task<SubscriptionPaymentOutcome> GrantStripeTermSwitch(UserSubscription subscription, StripeSubscriptionSnapshot snapshot,
        StripeInvoiceInfo invoice, SubscriptionTermSwitch row)
    {
        var reference = SubscriptionProviders.GrantReference(SubscriptionProviders.Stripe, invoice.Id);
        if (await IsGranted(subscription.User.ExternalId, reference))
        {
            // a redelivery; also completes a switch whose status change was lost after its grant
            await CompleteTermSwitch(row, TermSwitchStatus.Expired, TermSwitchStatus.Failed);
            return await StripeTermSwitchOutcome(subscription, snapshot, invoice, row);
        }
        if (snapshot.HasPendingUpdate)
            throw new ApiException($"Stripe has not applied the term switch paid by {invoice.Id} yet; retry later");
        if (StripeTermSwitchRejection(subscription, snapshot, invoice, row) is string rejection)
        {
            row.InvoiceId = invoice.Id;
            await FailTermSwitch(row);
            return await RejectStripeInvoice(subscription, invoice, rejection);
        }
        await PrepareOwnership(subscription);
        ApplyTermSwitchLocally(subscription, row, invoice.Id);
        ApplyStripePrice(subscription, snapshot);
        // The grant saves the switch together with the paid term
        await GrantPaidPeriod(subscription.User.ExternalId, (TopUpProduct)subscription.Product, subscription.ExternalId, reference);
        subscription.PaymentAmount = StripeAmounts.FromMinorUnits(invoice.AmountPaid, invoice.Currency).ToString("0.00", CultureInfo.InvariantCulture)
            + " " + invoice.Currency?.ToUpperInvariant();
        await context.SaveChangesAsync();
        // paid after it expired or failed: completed all the same, its promotion counted again
        await CompleteTermSwitch(row, TermSwitchStatus.Expired, TermSwitchStatus.Failed);
        logger.LogInformation("Stripe subscription {SubscriptionId} switched to {Plan} with invoice {InvoiceId}; access extended from {AccessEnd}",
            subscription.ExternalId, row.Product.Slug, invoice.Id, row.AccessEndsBefore);
        await TryScheduleStripeRenewal(subscription, snapshot);
        return await StripeTermSwitchOutcome(subscription, snapshot, invoice, row);
    }

    private static string StripeTermSwitchRejection(UserSubscription subscription, StripeSubscriptionSnapshot snapshot,
        StripeInvoiceInfo invoice, SubscriptionTermSwitch row)
    {
        if (StripePlans.SlugOf(snapshot) != row.Product.Slug)
            return $"paid for the switch to {row.Product.Slug}, but Stripe bills {StripePlans.SlugOf(snapshot)} (the pending update expired?)";
        if (subscription.ManagedPayments)
            return null;
        if (invoice.Total != row.ChargedCents)
            return $"total {invoice.Total} differs from the agreed term switch price {row.ChargedCents}";
        if (!string.Equals(invoice.Currency, row.Currency, StringComparison.OrdinalIgnoreCase))
            return $"currency {invoice.Currency} differs from {row.Currency}";
        return null;
    }

    /// <summary>The subscription is the new term now and continues as its 4-week plan afterwards (not saved here)</summary>
    private static void ApplyTermSwitchLocally(UserSubscription subscription, SubscriptionTermSwitch row, string paymentId)
    {
        subscription.Product = row.Product;
        subscription.RenewalProduct = row.RenewalProduct;
        subscription.RenewalPriceCents = row.RenewalCents;
        subscription.UpdatedAt = DateTime.UtcNow;
        row.InvoiceId = paymentId;
    }

    /// <summary>
    /// Confirmed like the purchase of the new term (not a renewal): the switch's declaration, the plan switched to and what the
    /// switch kept
    /// </summary>
    private async Task<SubscriptionPaymentOutcome> StripeTermSwitchOutcome(UserSubscription subscription, StripeSubscriptionSnapshot snapshot,
        StripeInvoiceInfo invoice, SubscriptionTermSwitch row)
    {
        var evidence = await StripeEvidence(subscription, snapshot, invoice, false);
        var outcome = StripeOutcome(subscription, snapshot, invoice, StripeAmounts.FromMinorUnits(row.ChargedCents, row.Currency)) with
        {
            Product = (TopUpProduct)row.Product, Evidence = evidence, IsProration = false, IsRenewal = false,
            TermSwitch = TermSwitchInfo(row, invoice.Created, await AccessExpiry(subscription))
        };
        var declaration = await TermSwitchDeclaration(row);
        return WithDeclaration(outcome, declaration, declaration, true);
    }
}
