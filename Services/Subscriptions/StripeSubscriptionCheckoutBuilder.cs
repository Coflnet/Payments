using System.Collections.Generic;
using Coflnet.Payments.Models;
using Microsoft.Extensions.Configuration;
using Stripe.Checkout;
using Keys = Coflnet.Payments.Services.SubscriptionService.StripeMetadata;

namespace Coflnet.Payments.Services.Subscriptions;

/// <summary>
/// Everything a Stripe subscription checkout session is built from
/// </summary>
public record StripeSubscriptionCheckout(
    User User,
    TopUpProduct Plan,
    long UnitAmountCents, // the recurring price. direct: gross incl. VAT, final; managed: net, Stripe adds the tax
    int IntervalWeeks,
    StripeCheckoutMode Mode,
    string Country,
    string SuccessUrl,
    string CancelUrl,
    string Email,
    string DiscountCode,
    string CreatorCode,
    int PaymentRequestId,
    long? FirstPaymentCents = null, // "first payment only" code: price of the first invoice, below UnitAmountCents
    string CouponId = null, // one-time Stripe coupon that lowers the first invoice to FirstPaymentCents
    string TaxRateId = null, // direct sales: inclusive manual VAT rate shown on Stripe's receipts
    string Region = null, // the buyer's state at checkout (evidence, per-state US cap)
    TopUpProduct RenewalPlan = null, // 13/52 week plans: the 4-week plan the subscription continues as after its first term
    long? RenewalUnitAmountCents = null); // 13/52 week plans: what every 4-week renewal charges (direct: gross, managed: net)

/// <summary>
/// Managed Payments settings (STRIPE:MANAGED_TAX_CODE, STRIPE:MANAGED_TAX_BEHAVIOR)
/// </summary>
public record StripeManagedSettings(string TaxCode, string TaxBehavior)
{
    /// <summary>Software as a service, personal use</summary>
    public const string DefaultTaxCode = "txcd_10103000";
    /// <summary>The unit amount is the net plan price; Stripe adds the buyer's tax at checkout (Lemon Squeezy era pricing)</summary>
    public const string DefaultTaxBehavior = "exclusive";

    public static StripeManagedSettings From(IConfiguration config) => new(
        config?["STRIPE:MANAGED_TAX_CODE"] ?? DefaultTaxCode,
        config?["STRIPE:MANAGED_TAX_BEHAVIOR"] ?? DefaultTaxBehavior);
}

/// <summary>
/// Builds subscription-mode checkout sessions. Direct and Managed Payments sessions have separate builders:
/// Managed Payments rejects many parameters (tax collection, payment method selection, address collection,
/// invoice settings, ...), so it only gets the small set it accepts plus tax_code/tax_behavior.
/// The price is always inline price_data holding the recurring price; a "first payment only" code is a one-time
/// coupon (<see cref="StripeFirstPaymentCoupon"/>), every other code lowers the unit amount.
/// 13 and 52 week plans carry their 4-week renewal plan and price in the metadata; a subscription schedule moves the
/// subscription to it once the first term is paid (<see cref="StripeRenewalSchedule"/>).
/// Both modes charge exactly what this service priced.
/// </summary>
public static class StripeSubscriptionCheckoutBuilder
{
    public static SessionCreateOptions Build(StripeSubscriptionCheckout checkout, StripeManagedSettings managed) =>
        checkout.Mode == StripeCheckoutMode.Managed ? BuildManaged(checkout, managed) : BuildDirect(checkout);

    private static SessionCreateOptions BuildDirect(StripeSubscriptionCheckout checkout)
    {
        var options = BaseOptions(checkout);
        // The amount is final: no tax_behavior, tax_code or automatic_tax, so Stripe adds nothing.
        var item = LineItem(checkout, null, null);
        if (checkout.TaxRateId != null)
            item.TaxRates = [checkout.TaxRateId]; // inclusive: the receipt shows rate and amount, the total stays the gross price
        options.LineItems = [item];
        // Billing country is the evidence for the VAT we charge and remit ourselves.
        options.BillingAddressCollection = "required";
        // Direct buyers always pay the EUR price: Adaptive Pricing would show and charge a local currency, which the
        // amount checks (EUR cents) and the VAT records do not expect. Not allowed on Managed Payments sessions.
        options.AdaptivePricing = new SessionAdaptivePricingOptions { Enabled = false };
        return options;
    }

    private static SessionCreateOptions BuildManaged(StripeSubscriptionCheckout checkout, StripeManagedSettings managed)
    {
        var options = BaseOptions(checkout);
        options.LineItems = [LineItem(checkout, managed.TaxCode, managed.TaxBehavior)];
        options.ManagedPayments = new SessionManagedPaymentsOptions { Enabled = true };
        return options;
    }

    private static SessionCreateOptions BaseOptions(StripeSubscriptionCheckout checkout)
    {
        var metadata = Metadata(checkout);
        return new SessionCreateOptions
        {
            Mode = "subscription",
            SubmitType = StripeSubscriptionDisclosure.SubmitType,
            CustomText = new SessionCustomTextOptions
            {
                Submit = new SessionCustomTextSubmitOptions { Message = StripeSubscriptionDisclosure.SubmitMessage(checkout) }
            },
            Discounts = checkout.CouponId == null ? null : [new SessionDiscountOptions { Coupon = checkout.CouponId }],
            Metadata = metadata,
            SubscriptionData = new SessionSubscriptionDataOptions { Metadata = new Dictionary<string, string>(metadata) },
            ClientReferenceId = checkout.User.ExternalId,
            CustomerEmail = checkout.Email,
            SuccessUrl = checkout.SuccessUrl,
            CancelUrl = checkout.CancelUrl
        };
    }

    private static SessionLineItemOptions LineItem(StripeSubscriptionCheckout checkout, string taxCode, string taxBehavior) => new()
    {
        Quantity = 1,
        PriceData = new SessionLineItemPriceDataOptions
        {
            Currency = checkout.Plan.CurrencyCode.ToLowerInvariant(),
            UnitAmount = checkout.UnitAmountCents,
            TaxBehavior = taxBehavior,
            Recurring = new SessionLineItemPriceDataRecurringOptions { Interval = "week", IntervalCount = checkout.IntervalWeeks },
            ProductData = new SessionLineItemPriceDataProductDataOptions
            {
                Name = checkout.Plan.Title,
                // 13/52 week plans: the renewal terms, so Stripe's "per 52 weeks" price line is not the only renewal statement
                Description = StripeSubscriptionDisclosure.LineItemDescription(checkout)
                    ?? (string.IsNullOrWhiteSpace(checkout.Plan.Description) ? null : checkout.Plan.Description),
                TaxCode = taxCode,
                Metadata = new Dictionary<string, string> { [Keys.ProductSlug] = checkout.Plan.Slug }
            }
        }
    };

    private static Dictionary<string, string> Metadata(StripeSubscriptionCheckout checkout)
    {
        var metadata = new Dictionary<string, string>
        {
            [Keys.UserId] = checkout.User.ExternalId,
            [Keys.ProductSlug] = checkout.Plan.Slug,
            [Keys.ProductId] = checkout.Plan.Id.ToString(),
            [Keys.PriceCents] = checkout.UnitAmountCents.ToString(),
            [Keys.Mode] = checkout.Mode == StripeCheckoutMode.Managed ? "managed" : "direct",
            [Keys.PaymentRequestId] = checkout.PaymentRequestId.ToString()
        };
        if (checkout.FirstPaymentCents < checkout.UnitAmountCents)
            metadata[Keys.FirstPriceCents] = checkout.FirstPaymentCents.ToString();
        AddIfSet(metadata, Keys.Country, checkout.Country);
        AddIfSet(metadata, Keys.Region, checkout.Region);
        AddIfSet(metadata, Keys.DiscountCode, checkout.DiscountCode);
        AddIfSet(metadata, Keys.CreatorCode, checkout.CreatorCode);
        if (checkout.RenewalPlan != null && checkout.RenewalUnitAmountCents != null)
        {
            metadata[Keys.RenewalProductSlug] = checkout.RenewalPlan.Slug;
            metadata[Keys.RenewalPriceCents] = checkout.RenewalUnitAmountCents.ToString();
        }
        return metadata;
    }

    private static void AddIfSet(Dictionary<string, string> metadata, string key, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            metadata[key] = value;
    }
}
