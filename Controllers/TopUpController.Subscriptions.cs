using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services;
using Coflnet.Payments.Services.Subscriptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Stripe.Checkout;

namespace Payments.Controllers;

/// <summary>
/// New subscriptions with the direct providers (Stripe, PayPal); Lemon Squeezy is the fallback where none applies.
/// </summary>
public partial class TopUpController
{
    /// <summary>
    /// The subscription providers a buyer from <paramref name="country"/> can choose for a new subscription.
    /// Lemon Squeezy is only listed when no direct provider applies.
    /// </summary>
    /// <param name="country">ISO 3166-1 alpha-2 country of the buyer</param>
    /// <param name="productSlug">Optional plan; PayPal is only listed when a PayPal plan is configured for it</param>
    [HttpGet]
    [Route("subscribe/providers")]
    public IEnumerable<SubscriptionProviderOption> GetSubscriptionProviders([FromQuery] string country, [FromQuery] string productSlug = null)
    {
        var options = subscriptionRouting.ProvidersFor(country);
        if (productSlug == null)
            return options;
        return options.Where(o => o.Provider != SubscriptionProviders.PayPal || paypalSubscriptions?.PlanIdFor(productSlug) != null);
    }

    /// <summary>
    /// Starts a Stripe subscription checkout. Buyers in SUBSCRIPTIONS:DIRECT_COUNTRIES buy from Coflnet directly
    /// (selected and IP country must match); everyone else buys through Stripe Managed Payments.
    /// </summary>
    /// <param name="userId">The user subscribing</param>
    /// <param name="productId">Slug of the subscription plan</param>
    /// <param name="options">Country, IP, urls, creator and discount code</param>
    [HttpPost]
    [Route("stripe/subscribe")]
    public async Task<TopUpIdResponse> StripeSubscribe(string userId, string productId, [FromBody] TopUpOptions options = null)
    {
        var stripe = stripeSubscriptions ?? throw new ApiException("Stripe subscriptions are not available.");
        var user = await userService.GetOrCreate(userId);
        var plan = await GetSubscriptionPlan(productId);
        var (country, mode) = await StripeCountryAndMode(options);
        var (price, _) = await SubscriptionPrice(options, plan, SubscriptionProviders.Stripe);
        var request = await AttemptBlockFraud(options, user, plan, price, SubscriptionProviders.Stripe);
        if (request.SessionId != null)
            return await ExistingStripeCheckout(stripe, request.SessionId);
        await RememberCountry(user, country, options);

        var checkout = new StripeSubscriptionCheckout(user, plan, SubscriptionPricing.ToCents(price),
            SubscriptionService.IntervalWeeks(plan.OwnershipSeconds).Value, mode, country,
            options?.SuccessUrl ?? config["DEFAULT:SUCCESS_URL"], options?.CancelUrl ?? config["DEFAULT:CANCEL_URL"],
            options?.UserEmail, options?.DiscountCode, options?.CreatorCode, request.Id);
        var session = await CreateStripeSubscriptionSession(stripe,
            StripeSubscriptionCheckoutBuilder.Build(checkout, StripeManagedSettings.From(config)));
        request.SessionId = session.Id;
        request.CoinAmount = plan.Cost;
        await db.SaveChangesAsync();
        return new TopUpIdResponse { Id = session.Id, DirectLink = session.Url };
    }

    /// <summary>
    /// Starts a PayPal subscription (direct sales only). Redirect the buyer to the returned approval link.
    /// </summary>
    /// <param name="userId">The user subscribing</param>
    /// <param name="productId">Slug of the subscription plan; needs PAYPAL:SUBSCRIPTION_PLANS:{slug}</param>
    /// <param name="options">Country, IP, urls, creator and discount code</param>
    [HttpPost]
    [Route("paypal/subscribe")]
    public async Task<TopUpIdResponse> PayPalSubscribe(string userId, string productId, [FromBody] TopUpOptions options = null)
    {
        var paypal = paypalSubscriptions ?? throw new ApiException("PayPal subscriptions are not available.");
        var user = await userService.GetOrCreate(userId);
        var plan = await GetSubscriptionPlan(productId);
        var planId = paypal.PlanIdFor(plan.Slug) ?? throw new ApiException("This plan is not available via PayPal.");
        var country = await GetMatchingCountry(options, false, "PayPal");
        EnsureCountryAllowed(country);
        if (!subscriptionRouting.PayPalAvailable(country))
            throw new ApiException($"PayPal subscriptions are not available in your country ({country}).");
        var (price, discount) = await SubscriptionPrice(options, plan, SubscriptionProviders.PayPal);
        // A PayPal plan override has one price for every cycle, so "first payment only" codes are Stripe only.
        if (discount?.Duration == DiscountCode.FirstPaymentOnly)
            throw new ApiException("This discount code is not available for PayPal subscriptions.");
        var request = await AttemptBlockFraud(options, user, plan, price, SubscriptionProviders.PayPal);
        if (request.SessionId != null)
            return await ExistingPayPalApproval(paypal, request.SessionId);
        await RememberCountry(user, country, options);

        var created = await paypal.Create(new PayPalSubscriptionRequest(planId,
            SubscriptionService.PayPalCustomId(plan.Id, user.ExternalId), price, plan.CurrencyCode.ToUpperInvariant(),
            options?.SuccessUrl ?? config["DEFAULT:SUCCESS_URL"], options?.CancelUrl ?? config["DEFAULT:CANCEL_URL"],
            options?.UserEmail));
        var approve = ApproveLink(created) ?? throw new ApiException("PayPal did not return an approval link.");
        // The webhook only activates subscriptions recorded here, for exactly this price.
        request.SessionId = created.Id;
        request.CoinAmount = plan.Cost;
        await db.SaveChangesAsync();
        return new TopUpIdResponse { Id = created.Id, DirectLink = approve };
    }

    private void EnsureLemonSqueezySubscriptionAllowed(string country)
    {
        if (subscriptionRouting.AllowsLemonSqueezyCheckout(country))
            return;
        throw new ApiException(subscriptionRouting.LemonSqueezyCheckoutEnabled
            ? "Please subscribe with one of the payment methods offered for your country."
            : "New Lemon Squeezy subscriptions are no longer available.");
    }

    private async Task<TopUpProduct> GetSubscriptionPlan(string productSlug)
    {
        var plan = await productService.GetTopupProduct(productSlug);
        if (!SubscriptionService.IsSubscriptionPlan(plan))
            throw new ApiException("Product is not a subscription plan");
        return new TopUpProduct(plan);
    }

    /// <summary>
    /// Direct countries get the one-time payment checks (selected country must match the IP country);
    /// Managed Payments buyers are taxed by Stripe, so their country only selects the mode.
    /// </summary>
    private async Task<(string country, StripeCheckoutMode mode)> StripeCountryAndMode(TopUpOptions options)
    {
        var selected = options?.Country?.Trim().ToUpperInvariant()
            ?? options?.Locale?.Split(',').FirstOrDefault()?.Split('-').LastOrDefault()?.ToUpperInvariant();
        var country = selected?.Length == 2 ? selected : await ipCountryLookup.GetCountry(options?.UserIp);
        if (subscriptionRouting.IsDirectCountry(country))
        {
            country = await GetMatchingCountry(options, false, "Stripe");
            EnsureCountryAllowed(country);
        }
        var mode = subscriptionRouting.StripeMode(country)
            ?? throw new ApiException($"Stripe subscriptions are not available in your country ({country ?? "unknown"}).");
        return (country, mode);
    }

    private async Task<(decimal price, Coflnet.Payments.Models.LemonSqueezy.ValidatedDiscount discount)> SubscriptionPrice(
        TopUpOptions options, TopUpProduct plan, string provider)
    {
        if ((options?.TopUpAmount ?? 0) > 0)
            throw new ApiException("Subscriptions have a fixed price.");
        // GetPriceAndCoins applies the creator code; the discount code is applied by the direct providers themselves.
        var (creatorPrice, _, _, discount) = await GetPriceAndCoins(options, plan, isSubscription: true, checkoutProvider: provider);
        var price = SubscriptionPricing.Apply(creatorPrice, null, discount);
        if (SubscriptionPricing.ToCents(price) < SubscriptionPricing.MinimumCents)
            throw new ApiException("The discounted price is below the minimum charge.");
        return (price, discount);
    }

    private async Task RememberCountry(User user, string country, TopUpOptions options)
    {
        if (country != null && user.Country != country)
            user.Country = country;
        if (user.Locale == null && options?.Locale != null)
            user.Locale = options.Locale;
        await db.SaveChangesAsync();
    }

    private async Task<Session> CreateStripeSubscriptionSession(IStripeSubscriptionGateway stripe, SessionCreateOptions options)
    {
        try
        {
            return await stripe.CreateCheckoutSession(options);
        }
        catch (Stripe.StripeException e) when (StripeErrorClassifier.IsCredentialError(e.HttpStatusCode))
        {
            PaymentMetrics.StripeCredentialErrors.Inc();
            _logger.LogCritical(e, "Stripe subscription checkout was rejected by Stripe credentials (HTTP {StatusCode}, Stripe code {StripeCode})",
                e.HttpStatusCode, e.StripeError?.Code);
            throw new ApiException(StripeErrorClassifier.UserMessage);
        }
        catch (Exception e) when (e is not ApiException)
        {
            _logger.LogError(e, "Stripe subscription checkout session could not be created");
            throw new ApiException("Stripe payment is temporarily unavailable. Please try again later.");
        }
    }

    private static async Task<TopUpIdResponse> ExistingStripeCheckout(IStripeSubscriptionGateway stripe, string sessionId)
    {
        var existing = await stripe.GetCheckoutSession(sessionId);
        if (string.IsNullOrWhiteSpace(existing.Url))
            throw new ApiException("The existing Stripe checkout session is no longer active. Please try again later.");
        return new TopUpIdResponse { Id = existing.Id, DirectLink = existing.Url };
    }

    private static async Task<TopUpIdResponse> ExistingPayPalApproval(IPayPalSubscriptionApi paypal, string subscriptionId)
    {
        var existing = await paypal.Get(subscriptionId);
        var approve = existing?.Status == "APPROVAL_PENDING" ? ApproveLink(existing) : null;
        if (approve == null)
            throw new ApiException("A PayPal subscription checkout is already in progress. Please try again later.");
        return new TopUpIdResponse { Id = existing.Id, DirectLink = approve };
    }

    private static string ApproveLink(Coflnet.Payments.Models.PayPal.PayPalSubscription subscription) =>
        subscription?.Links?.FirstOrDefault(l => l.Rel == "approve")?.Href;
}
