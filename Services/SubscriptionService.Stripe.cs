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
        /// <summary>The recurring price</summary>
        public const string PriceCents = "priceCents";
        /// <summary>The price of the first invoice, set when a "first payment only" code lowers it (a one-time coupon)</summary>
        public const string FirstPriceCents = "firstPriceCents";
        public const string Mode = "mode";
        public const string Country = "country";
        /// <summary>The buyer's state at checkout (ISO 3166-2 code without country prefix)</summary>
        public const string Region = "region";
        public const string DiscountCode = "discountCode";
        public const string CreatorCode = "creatorCode";
        public const string PaymentRequestId = "paymentRequestId";
        /// <summary>13/52 week plans: the 4-week plan the subscription continues as after its first term</summary>
        public const string RenewalProductSlug = "renewalProductSlug";
        /// <summary>13/52 week plans: what every 4-week renewal charges (direct: gross, Managed Payments: net)</summary>
        public const string RenewalPriceCents = "renewalPriceCents";
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
            await TryScheduleStripeRenewal(subscription, snapshot);
            return true;
        });
        // Delayed payment methods complete the checkout unpaid; invoice.paid grants once the money arrived.
        if (recorded && session.PaymentStatus == "paid")
            await MarkCheckoutPaid(SubscriptionProviders.Stripe, session.Id);
    }

    /// <summary>
    /// customer.subscription.updated/deleted and invoice.payment_failed: synchronizes status, EndsAt and RenewsAt.
    /// A 13 or 52 week subscription that will renew without our renewal schedule (e.g. resumed elsewhere) is scheduled again.
    /// </summary>
    public Task StripeSubscriptionChanged(string subscriptionId) => WithSubscriptionLock(subscriptionId, async () =>
    {
        var snapshot = await StripeApi.GetSubscription(subscriptionId);
        var subscription = await EnsureStripeSubscription(snapshot);
        if (subscription == null)
            return false;
        ApplyStripeState(subscription, snapshot);
        await context.SaveChangesAsync();
        await TryScheduleStripeRenewal(subscription, snapshot);
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
        // A redelivery of a granted invoice is the same period payment again, whatever the plan is now (the first 4-week
        // invoice, billing reason "updated", would otherwise look like a proration once the subscription is converted)
        var extension = await IsExtensionInvoice(subscription, snapshot, invoice);
        if (await IsGranted(subscription.User.ExternalId, SubscriptionProviders.GrantReference(SubscriptionProviders.Stripe, invoice.Id)))
            return await GrantedStripeOutcome(subscription, snapshot, invoice, false, extension);
        return invoice.NormalizedBillingReason switch
        {
            "initial" or "renewal" => await GrantStripeInvoice(subscription, snapshot, invoice, extension),
            // The first 4-week invoice after the renewal schedule's phase change (Stripe does not document its billing reason),
            // or the first invoice of the extension term the buyer opted in to
            "updated" when BillsRenewalPlan(subscription, StripePlans.SlugOf(snapshot)) || extension
                => await GrantStripeInvoice(subscription, snapshot, invoice, extension),
            "updated" => await RecordStripeProration(subscription, snapshot, invoice),
            _ => await RejectStripeInvoice(subscription, invoice, $"unexpected billing reason {invoice.BillingReason}")
        };
    }

    /// <param name="extension">The invoice is the first one of an extension term (<see cref="IsExtensionInvoice"/>)</param>
    private async Task<SubscriptionPaymentOutcome> GrantStripeInvoice(UserSubscription subscription,
        StripeSubscriptionSnapshot snapshot, StripeInvoiceInfo invoice, bool extension)
    {
        var reference = SubscriptionProviders.GrantReference(SubscriptionProviders.Stripe, invoice.Id);
        if (await IsGranted(subscription.User.ExternalId, reference))
            return await GrantedStripeOutcome(subscription, snapshot, invoice, false, extension); // a redelivery: recording is retried
        if (!await ReconcileStripePlan(subscription, snapshot))
            return await RejectStripeInvoice(subscription, invoice,
                $"Stripe bills plan {StripePlans.SlugOf(snapshot)}, which is no valid change from {subscription.Product.Slug}");
        var rejection = StripeAmountRejection(subscription, snapshot, invoice, extension);
        if (rejection != null)
            return await RejectStripeInvoice(subscription, invoice, rejection);
        var evidence = await StripeEvidence(subscription, snapshot, invoice, invoice.NormalizedBillingReason == "initial");
        if (invoice.NormalizedBillingReason == "initial" && !await AcceptsDirectBuyer(subscription, invoice, evidence))
            return null;
        WarnOnLongRenewal(subscription, invoice, extension);
        await GrantPaidPeriod(subscription.User.ExternalId, (TopUpProduct)subscription.Product, subscription.ExternalId, reference);
        subscription.PaymentAmount = StripeAmounts.FromMinorUnits(invoice.AmountPaid, invoice.Currency).ToString("0.00", CultureInfo.InvariantCulture) + " " + invoice.Currency?.ToUpperInvariant();
        await context.SaveChangesAsync();
        await TryScheduleStripeRenewal(subscription, snapshot);
        // A granted invoice pays a period, also the first 4-week one after the renewal schedule (billing reason "updated")
        return await WithStripeDeclaration(StripeOutcome(subscription, snapshot, invoice, AgreedAmount(subscription, snapshot, invoice, extension))
            with { Evidence = evidence, IsProration = false }, snapshot, invoice, true, extension);
    }

    private async Task<SubscriptionPaymentOutcome> GrantedStripeOutcome(UserSubscription subscription,
        StripeSubscriptionSnapshot snapshot, StripeInvoiceInfo invoice, bool cardCountryRequired, bool extension)
    {
        var evidence = await StripeEvidence(subscription, snapshot, invoice, cardCountryRequired);
        // A granted invoice pays a period, also the first 4-week one after the renewal schedule (billing reason "updated")
        return await WithStripeDeclaration(StripeOutcome(subscription, snapshot, invoice, AgreedAmount(subscription, snapshot, invoice, extension))
            with { Evidence = evidence, IsProration = false }, snapshot, invoice, true, extension);
    }

    /// <summary>
    /// A 13 or 52 week subscription renewed for its full period: its renewal schedule was missing. The payment is granted
    /// (the buyer paid for it), but the renewal clause was not lawful; the difference has to be settled by hand.
    /// An extension term the buyer opted in to is no such renewal.
    /// </summary>
    private void WarnOnLongRenewal(UserSubscription subscription, StripeInvoiceInfo invoice, bool extension)
    {
        if (invoice.NormalizedBillingReason != "renewal" || !SubscriptionRenewal.ContinuesAsFourWeeks(subscription.Product) || extension)
            return;
        logger.LogError("Stripe invoice {InvoiceId} renewed {SubscriptionId} for another {Plan} term instead of continuing as {RenewalPlan} every 4 weeks; refund the difference",
            invoice.Id, subscription.ExternalId, subscription.Product.Slug, subscription.RenewalProduct?.Slug ?? "(no renewal plan found)");
    }

    /// <summary>
    /// Place of supply evidence of a Stripe payment. Managed Payments is taxed by Stripe, so the card is only looked up for
    /// direct sales; there it is required for the first invoice (its verdict decides the grant) and best effort afterwards.
    /// </summary>
    private async Task<PaymentEvidence> StripeEvidence(UserSubscription subscription, StripeSubscriptionSnapshot snapshot,
        StripeInvoiceInfo invoice, bool cardCountryRequired)
    {
        var card = subscription.ManagedPayments ? null : await CardCountry(invoice, cardCountryRequired);
        var requestId = int.TryParse(snapshot.Metadata.GetValueOrDefault(StripeMetadata.PaymentRequestId), out var id) ? id : 0;
        var ip = await context.PaymentRequests.Where(r => r.Id == requestId).Select(r => r.CreateOnIp).FirstOrDefaultAsync();
        return new PaymentEvidence(ip?.ToString(), MetadataCountry(snapshot), invoice.CustomerCountry, card,
            subscription.CheckoutRegion, invoice.CustomerState);
    }

    private async Task<string> CardCountry(StripeInvoiceInfo invoice, bool required)
    {
        try
        {
            return await StripeApi.GetCardCountry(invoice.Id);
        }
        catch (Exception e) when (!required)
        {
            logger.LogWarning(e, "Could not read the card country of Stripe invoice {InvoiceId}", invoice.Id);
            return null;
        }
    }

    /// <summary>
    /// The outcome reports the EUR amount without VAT (creator shares are based on net revenue).
    /// Direct sales (EUR, amount verified) split the VAT of the checkout country off what was paid and are reported
    /// under that country; Managed Payments reports the tax Stripe remitted and <paramref name="managedEurAmount"/>,
    /// the already net agreed price (null when unknown).
    /// </summary>
    private SubscriptionPaymentOutcome StripeOutcome(UserSubscription subscription, StripeSubscriptionSnapshot snapshot,
        StripeInvoiceInfo invoice, decimal? managedEurAmount)
    {
        var paid = StripeAmounts.FromMinorUnits(invoice.AmountPaid, invoice.Currency);
        if (subscription.ManagedPayments)
            return StripeOutcome(subscription, invoice, paid, invoice.CustomerCountry ?? MetadataCountry(snapshot),
                managedEurAmount, StripeAmounts.FromMinorUnits(invoice.Tax, invoice.Currency), 0) with { PriceTaxBehavior = snapshot.TaxBehavior };
        var country = MetadataCountry(snapshot) ?? invoice.CustomerCountry;
        var (net, vat, rate) = VatSplit(paid, country);
        return StripeOutcome(subscription, invoice, paid, country, net, vat, rate);
    }

    private static SubscriptionPaymentOutcome StripeOutcome(UserSubscription subscription, StripeInvoiceInfo invoice,
        decimal paid, string country, decimal? eurNet, decimal vat, double rate) =>
        new(subscription, (TopUpProduct)subscription.Product, invoice.Id, paid,
            invoice.Currency?.ToUpperInvariant(), country,
            invoice.CustomerPostalCode, invoice.CustomerEmail, invoice.CustomerName, invoice.Created, eurNet,
            invoice.NormalizedBillingReason == "updated", vat, rate, IsRenewal: invoice.NormalizedBillingReason != "initial");

    private async Task<SubscriptionPaymentOutcome> RejectStripeInvoice(UserSubscription subscription, StripeInvoiceInfo invoice, string reason)
    {
        await MarkForManualRefund(SubscriptionProviders.Stripe, invoice.Id, subscription, StripeAmounts.FromMinorUnits(invoice.AmountPaid, invoice.Currency),
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
        if (BillsRenewalPlan(subscription, slug))
        {
            ConvertToRenewalPlan(subscription);
            return true;
        }
        var target = await PlanBySlug(slug);
        if (target == null || !CanChangePlan(subscription.Product, target))
            return false;
        logger.LogWarning("Stripe subscription {SubscriptionId} bills {Billed} while {Local} was recorded; applying Stripe's plan",
            subscription.ExternalId, slug, subscription.Product.Slug);
        await PrepareOwnership(subscription);
        await ApplyPlanLocally(subscription, target, null, DateTime.UtcNow, s => ApplyStripePrice(s, snapshot));
        return true;
    }

    /// <summary>The agreed amount of an invoice in major units of the subscription's currency (null when unknown)</summary>
    private static decimal? AgreedAmount(UserSubscription subscription, StripeSubscriptionSnapshot snapshot, StripeInvoiceInfo invoice,
        bool extension) =>
        AgreedCents(subscription, snapshot, invoice, extension) is long cents ? StripeAmounts.FromMinorUnits(cents, subscription.Currency) : null;

    /// <summary>
    /// What an invoice must charge: the recurring price, except the first invoice of a "first payment only" code,
    /// which the checkout's one-time coupon lowers to the discounted price (checkout metadata), and the first invoice of an
    /// extension term, which charges the price the extension was booked at.
    /// </summary>
    private static long? AgreedCents(UserSubscription subscription, StripeSubscriptionSnapshot snapshot, StripeInvoiceInfo invoice,
        bool extension)
    {
        if (extension)
            return subscription.ExtensionPriceCents;
        if (invoice.NormalizedBillingReason == "initial"
            && long.TryParse(snapshot.Metadata.GetValueOrDefault(StripeMetadata.FirstPriceCents), out var first)
            && first < subscription.PriceCents)
            return first;
        return subscription.PriceCents;
    }

    /// <summary>
    /// Direct invoices must charge exactly the price this service set. Managed Payments may charge in the
    /// buyer's local currency (Adaptive Pricing), so only the plan is verified for those.
    /// </summary>
    private static string StripeAmountRejection(UserSubscription subscription, StripeSubscriptionSnapshot snapshot, StripeInvoiceInfo invoice,
        bool extension)
    {
        if (subscription.ManagedPayments)
            return null;
        var agreed = AgreedCents(subscription, snapshot, invoice, extension);
        if (agreed == null || invoice.Total != agreed)
            return $"total {invoice.Total} differs from the agreed price {agreed}";
        if (!string.Equals(invoice.Currency, subscription.Currency, StringComparison.OrdinalIgnoreCase))
            return $"currency {invoice.Currency} differs from {subscription.Currency}";
        return null;
    }

    /// <summary>
    /// Like one-time payments, a direct sale to a country we can not sell to is not fulfilled:
    /// the subscription ends immediately and the payment is marked for a manual refund. So is a sale whose billing address
    /// and card agree on a country other than the one whose VAT the price contains (the buyer is not where the checkout
    /// claimed, so the VAT charged would be wrong).
    /// </summary>
    private async Task<bool> AcceptsDirectBuyer(UserSubscription subscription, StripeInvoiceInfo invoice, PaymentEvidence evidence)
    {
        if (subscription.ManagedPayments)
            return true;
        var country = invoice.CustomerCountry ?? subscription.User.Country;
        if (country == null || !SalesRestrictions.DoWeSellTo(country, invoice.CustomerPostalCode))
            return await RejectDirectBuyer(subscription, invoice, $"buyer country {country ?? "UNKNOWN"} is not sold to directly; subscription cancelled");
        if (!ContradictsCheckoutCountry(evidence))
            return true;
        logger.LogError("Stripe invoice {InvoiceId}: billing address and card are in {Country} but the VAT country of the checkout is {CheckoutCountry}",
            invoice.Id, evidence.BillingCountry, evidence.CheckoutCountry);
        return await RejectDirectBuyer(subscription, invoice,
            $"billing address and card country {evidence.BillingCountry} differ from the checkout (VAT) country {evidence.CheckoutCountry}; subscription cancelled");
    }

    private static bool ContradictsCheckoutCountry(PaymentEvidence evidence) =>
        evidence.CheckoutCountry != null && evidence.BillingCountry != null && evidence.PaymentMethodCountry != null
        && evidence.BillingCountry == evidence.PaymentMethodCountry && evidence.BillingCountry != evidence.CheckoutCountry;

    private async Task<bool> RejectDirectBuyer(UserSubscription subscription, StripeInvoiceInfo invoice, string reason)
    {
        ApplyStripeState(subscription, await StripeApi.CancelNow(subscription.ExternalId));
        await RejectStripeInvoice(subscription, invoice, reason);
        return false;
    }

    /// <summary>
    /// Proration invoices pay for an upgrade, not another period, but are revenue to record.
    /// The plan change itself is applied by <see cref="ChangePlan"/> when the proration is paid at once;
    /// a delayed payment applies the pending update later and the change is recorded here.
    /// </summary>
    private async Task<SubscriptionPaymentOutcome> RecordStripeProration(UserSubscription subscription,
        StripeSubscriptionSnapshot snapshot, StripeInvoiceInfo invoice)
    {
        // A Managed Payments proration may be paid in a local currency: its EUR amount is unknown.
        if (await context.SubscriptionPlanChanges.AnyAsync(c => c.InvoiceId == invoice.Id))
            return await WithStripeDeclaration(StripeOutcome(subscription, snapshot, invoice, null), snapshot, invoice, false, false);
        if (snapshot.HasPendingUpdate)
            throw new ApiException($"Stripe has not applied the upgrade paid by {invoice.Id} yet; retry later");
        var target = await PlanBySlug(StripePlans.SlugOf(snapshot));
        if (target == null || target.Id != subscription.Product.Id && !CanChangePlan(subscription.Product, target))
            return await RejectStripeInvoice(subscription, invoice, "paid proration without a valid plan change");
        if (IsConvertedRenewalPeriod(subscription, target, snapshot, invoice))
            return await GrantStripeInvoice(subscription, snapshot, invoice, false);
        if (target.Id == subscription.Product.Id && ChargesFullTerm(snapshot, invoice))
            return await RejectStripeInvoice(subscription, invoice,
                "an updated invoice charged a full term of the current plan without a plan change (an extension phase nobody booked?)");
        if (target.Id != subscription.Product.Id)
        {
            await PrepareOwnership(subscription);
            await ApplyPlanLocally(subscription, target, invoice.Id, invoice.Created, s => ApplyStripePrice(s, snapshot));
            await TryScheduleStripeRenewal(subscription, snapshot); // the paid upgrade continues as the 4-week plan of its tier
        }
        return await WithStripeDeclaration(StripeOutcome(subscription, snapshot, invoice, null), snapshot, invoice, false, false);
    }

    /// <summary>
    /// A full 4-week invoice ("updated", without a plan change) of the renewal plan a 13/52 week subscription converted to is a
    /// period payment, not a proration: the first 4-week invoice after the renewal schedule's phase change, arriving after the
    /// conversion was saved (a retry after a failure, or a later cycle invoice paid first, e.g. after a card update). Stripe
    /// bills each period once, so every such invoice pays a period of its own and is granted (once per invoice; a direct one
    /// must charge the agreed renewal price).
    /// </summary>
    private static bool IsConvertedRenewalPeriod(UserSubscription subscription, TopUpProduct target, StripeSubscriptionSnapshot snapshot,
        StripeInvoiceInfo invoice) =>
        target.Id == subscription.Product.Id && subscription.RenewalProduct?.Id == target.Id && ChargesFullTerm(snapshot, invoice);

    /// <summary>
    /// A proration only charges part of a term; an "updated" invoice of at least the whole recurring price is a term (e.g. an
    /// extension phase left at Stripe after its booking was dropped), which is not granted without a booking
    /// </summary>
    private static bool ChargesFullTerm(StripeSubscriptionSnapshot snapshot, StripeInvoiceInfo invoice) =>
        snapshot.UnitAmount is long unit && unit > 0 && invoice.Total >= unit
        && string.Equals(invoice.Currency, snapshot.Currency, StringComparison.OrdinalIgnoreCase);

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
            CheckoutCountry = MetadataCountry(snapshot)?.ToUpperInvariant(),
            CheckoutRegion = UsDirectSubscriptionCap.NormalizeRegion(snapshot.Metadata.GetValueOrDefault(StripeMetadata.Region)),
            PriceCents = long.TryParse(metadata.GetValueOrDefault(StripeMetadata.PriceCents), out var cents) ? cents : null,
            Currency = product.CurrencyCode?.ToUpperInvariant(),
            DiscountCode = metadata.GetValueOrDefault(StripeMetadata.DiscountCode),
            CreatorCode = metadata.GetValueOrDefault(StripeMetadata.CreatorCode)
        };
        await ApplyCheckoutRenewalTerms(subscription, metadata);
        ApplyStripeState(subscription, snapshot);
        return await GetOrCreateSubscriptionAsync(subscription);
    }

    /// <summary>
    /// The renewal terms the checkout showed (13/52 week plans). Missing or invalid ones are determined later
    /// (<see cref="EnsureRenewalTerms"/>) like those of subscriptions sold before the 4-week renewal.
    /// </summary>
    private async Task ApplyCheckoutRenewalTerms(UserSubscription subscription, IReadOnlyDictionary<string, string> metadata)
    {
        if (!SubscriptionRenewal.ContinuesAsFourWeeks(subscription.Product))
            return;
        var renewal = await PlanBySlug(metadata.GetValueOrDefault(StripeMetadata.RenewalProductSlug));
        if (!SubscriptionRenewal.IsRenewalOf(subscription.Product, renewal)
            || !long.TryParse(metadata.GetValueOrDefault(StripeMetadata.RenewalPriceCents), out var cents))
            return;
        subscription.RenewalProduct = renewal;
        subscription.RenewalPriceCents = cents;
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
        // A cancelled subscription has no extension (one cancelled elsewhere must not come back with a resume)
        if (subscription.EndsAt != null && HasPendingExtension(subscription, DateTime.UtcNow))
            (subscription.ExtensionStartsAt, subscription.ExtensionPriceCents) = (null, null);
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
    /// "first payment only" codes no longer apply to recurring prices), in the amount Stripe charges:
    /// gross with the VAT of the checkout <paramref name="country"/> for direct subscriptions, net for Managed Payments.
    /// Null when a direct subscription's country has no VAT rate.
    /// </summary>
    private async Task<long?> RecurringPriceCents(UserSubscription subscription, TopUpProduct plan, string country)
    {
        var creator = string.IsNullOrWhiteSpace(subscription.CreatorCode) ? null
            : await context.CreatorCodes.FirstOrDefaultAsync(c => c.Code.ToLower() == subscription.CreatorCode.ToLower());
        var discount = (await DiscountTerms(subscription.DiscountCode))?.ForPeriod(IntervalWeeks(plan.OwnershipSeconds));
        if (discount?.Duration == DiscountCode.FirstPaymentOnly)
            discount = null;
        return ChargedCents(SubscriptionPricing.Apply(plan.Price, creator, discount), subscription.ManagedPayments, country);
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

        // Releasing the renewal schedule also drops an extension booked in it
        public async Task CancelAtPeriodEnd(UserSubscription subscription)
        {
            if (MayHaveRenewalSchedule(subscription))
                await service.ReleaseStripeRenewal(subscription, await service.StripeApi.GetSubscription(subscription.ExternalId));
            ApplyStripeState(subscription, await service.StripeApi.SetCancelAtPeriodEnd(subscription.ExternalId, true));
            if (subscription.Status != "cancelled" || subscription.EndsAt == null)
                throw new ApiException("Stripe did not confirm the cancellation.");
        }

        public async Task Resume(UserSubscription subscription)
        {
            var resumed = await service.StripeApi.SetCancelAtPeriodEnd(subscription.ExternalId, false);
            ApplyStripeState(subscription, resumed);
            if (subscription.Status != "active")
                throw new ApiException("Stripe did not confirm reactivation.");
            await service.ScheduleResumedStripeRenewal(subscription, resumed);
        }

        public async Task<SubscriptionChangeResult> ChangePlan(UserSubscription subscription, TopUpProduct target)
        {
            var current = await service.StripeApi.GetSubscription(subscription.ExternalId);
            if (current.Status != "active" || current.CancelAtPeriodEnd || current.CancelAt != null || current.HasPendingUpdate
                || current.ItemId == null)
                throw new ApiException("The subscription is not available for a plan change. Reload its status first.");
            await service.PrepareOwnership(subscription);
            var upgrade = TierRank(target) > TierRank(await service.AccessProduct(subscription));
            var priceCents = await service.RecurringPriceCents(subscription, target, MetadataCountry(current))
                ?? throw new ApiException($"The plan can not be priced for country {MetadataCountry(current)}: no VAT rate is configured.");
            if (InFirstLongTerm(subscription))
                await service.renewalPlans.Require(target); // it must be able to continue every 4 weeks as well
            current = await service.ReleaseStripeRenewal(subscription, current);
            var change = await service.StripeApi.ChangePrice(current, new StripePriceChangeRequest(priceCents,
                target.CurrencyCode.ToLowerInvariant(), IntervalWeeks(target.OwnershipSeconds).Value, upgrade,
                current.TaxBehavior, target.Slug, target.Title));
            // An unpaid upgrade stays a pending update at Stripe; invoice.paid applies it once the payment succeeds.
            if (upgrade && change.InvoiceStatus != "paid")
                return new("pending");
            await service.ApplyPlanLocally(subscription, target, upgrade ? change.InvoiceId : null, DateTime.UtcNow,
                s => ApplyStripePrice(s, change.Subscription));
            await service.TryScheduleStripeRenewal(subscription, change.Subscription);
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
