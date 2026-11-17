using System;
using System.Collections.Generic;
using Coflnet.Payments.Models;
using Microsoft.Extensions.Configuration;
using Stripe.Checkout;
using Keys = Coflnet.Payments.Services.SubscriptionService.StripeMetadata;

namespace Coflnet.Payments.Services.Subscriptions;

/// <summary>
/// Everything a Stripe subscription checkout session is built from
/// </summary>
/// <param name="ExpiresAt">When the session must expire (see <see cref="StripeSubscriptionCheckoutBuilder.SessionExpiry"/>);
/// null keeps Stripe's default of 24 hours</param>
public record StripeSubscriptionCheckout(
    User User,
    TopUpProduct Plan,
    long UnitAmountCents,
    int IntervalWeeks,
    StripeCheckoutMode Mode,
    string Country,
    string SuccessUrl,
    string CancelUrl,
    string Email,
    string DiscountCode,
    string CreatorCode,
    int PaymentRequestId,
    DateTime? ExpiresAt = null);

/// <summary>
/// Managed Payments settings (STRIPE:MANAGED_TAX_CODE, STRIPE:MANAGED_TAX_BEHAVIOR)
/// </summary>
public record StripeManagedSettings(string TaxCode, string TaxBehavior)
{
    /// <summary>Software as a service, personal use</summary>
    public const string DefaultTaxCode = "txcd_10103000";
    /// <summary>Buyers pay the list price; Stripe withholds the tax from it</summary>
    public const string DefaultTaxBehavior = "inclusive";

    public static StripeManagedSettings From(IConfiguration config) => new(
        config?["STRIPE:MANAGED_TAX_CODE"] ?? DefaultTaxCode,
        config?["STRIPE:MANAGED_TAX_BEHAVIOR"] ?? DefaultTaxBehavior);
}

/// <summary>
/// Builds subscription-mode checkout sessions. Direct and Managed Payments sessions have separate builders:
/// Managed Payments rejects many parameters (tax collection, payment method selection, address collection,
/// invoice settings, ...), so it only gets the small set it accepts plus tax_code/tax_behavior.
/// The price is always inline price_data (discounts lower the unit amount, no Stripe coupons), so both modes
/// charge exactly what this service priced.
/// </summary>
public static class StripeSubscriptionCheckoutBuilder
{
    /// <summary>Stripe accepts expires_at from 30 minutes to 24 hours after creation; one minute of margin for latency</summary>
    private static readonly TimeSpan ShortestSession = TimeSpan.FromMinutes(31);
    private static readonly TimeSpan LongestSession = TimeSpan.FromHours(24) - TimeSpan.FromMinutes(1);

    /// <summary>
    /// The expires_at of a session priced with a discount code that expires at <paramref name="priceValidUntil"/>:
    /// the code's expiry, but at least 31 minutes from now (Stripe's minimum) and null (Stripe's default of 24 hours)
    /// when the code outlives that anyway.
    /// </summary>
    public static DateTime? SessionExpiry(DateTime? priceValidUntil, DateTime now)
    {
        if (priceValidUntil is not DateTime expiry)
            return null;
        var until = expiry.Kind == DateTimeKind.Local ? expiry.ToUniversalTime() : DateTime.SpecifyKind(expiry, DateTimeKind.Utc);
        if (until >= now + LongestSession)
            return null;
        return until < now + ShortestSession ? now + ShortestSession : until;
    }

    public static SessionCreateOptions Build(StripeSubscriptionCheckout checkout, StripeManagedSettings managed) =>
        checkout.Mode == StripeCheckoutMode.Managed ? BuildManaged(checkout, managed) : BuildDirect(checkout);

    private static SessionCreateOptions BuildDirect(StripeSubscriptionCheckout checkout)
    {
        var options = BaseOptions(checkout);
        options.LineItems = [LineItem(checkout, null, null)];
        // Billing country is the evidence for the VAT we charge and remit ourselves.
        options.BillingAddressCollection = "required";
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
            Metadata = metadata,
            SubscriptionData = new SessionSubscriptionDataOptions { Metadata = new Dictionary<string, string>(metadata) },
            ClientReferenceId = checkout.User.ExternalId,
            CustomerEmail = checkout.Email,
            ExpiresAt = checkout.ExpiresAt,
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
                Description = string.IsNullOrWhiteSpace(checkout.Plan.Description) ? null : checkout.Plan.Description,
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
        AddIfSet(metadata, Keys.Country, checkout.Country);
        AddIfSet(metadata, Keys.DiscountCode, checkout.DiscountCode);
        AddIfSet(metadata, Keys.CreatorCode, checkout.CreatorCode);
        return metadata;
    }

    private static void AddIfSet(Dictionary<string, string> metadata, string key, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            metadata[key] = value;
    }
}
