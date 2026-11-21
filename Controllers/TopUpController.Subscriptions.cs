using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Models.LemonSqueezy;
using Coflnet.Payments.Services;
using Coflnet.Payments.Services.Subscriptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
    /// <param name="productSlug">Optional plan; PayPal is only listed when the plan has a billing period PayPal supports</param>
    /// <param name="region">Optional state of the buyer (ISO 3166-2 code without country prefix, e.g. "CA"); the US direct cap applies per state,
    /// a US buyer without it is routed like one from a capped state</param>
    [HttpGet]
    [Route("subscribe/providers")]
    public async Task<IEnumerable<SubscriptionProviderOption>> GetSubscriptionProviders([FromQuery] string country, [FromQuery] string productSlug = null,
        [FromQuery] string region = null)
    {
        await subscriptionRouting.RefreshAsync();
        var options = subscriptionRouting.Capture(region).ProvidersFor(country);
        if (productSlug == null || paypalSubscriptions == null)
            return options;
        var plan = await productService.GetTopupProduct(productSlug, false);
        var payPalSellable = SubscriptionService.IsSubscriptionPlan(plan) && PayPalPlanCatalog.IsSellable(new TopUpProduct(plan));
        return options.Where(o => o.Provider != SubscriptionProviders.PayPal || payPalSellable);
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
        await subscriptionRouting.RefreshAsync();
        var user = await userService.GetOrCreate(userId);
        var plan = await GetSubscriptionPlan(productId);
        declarations.Validate(options, plan, userId);
        var region = UsDirectSubscriptionCap.NormalizeRegion(options?.Region);
        var (country, mode, vatBasisPoints) = await StripeCountryAndMode(options, subscriptionRouting.Capture(region));
        var priced = await SubscriptionPrice(options, plan, SubscriptionProviders.Stripe, vatBasisPoints);
        var request = await AttemptBlockFraud(options, user, plan, priced.First, SubscriptionProviders.Stripe,
            RecurringIfDifferent(priced.First, priced.Following), country, mode == StripeCheckoutMode.Managed, region);
        if (request.SessionId != null)
            return await ExistingStripeCheckout(stripe, request.SessionId);
        await RememberCountry(user, country, options);
        await declarations.Record(options, user, plan, request);
        var trialDays = await trials.DaysFor(options, plan, user, priced.Discount);

        var checkout = new StripeSubscriptionCheckout(user, plan,
            SubscriptionPricing.ToCents(priced.Recurring), // gross for direct sales, net for Managed Payments (see SubscriptionPrice)
            SubscriptionService.IntervalWeeks(plan.OwnershipSeconds).Value, mode, country,
            options?.SuccessUrl ?? config["DEFAULT:SUCCESS_URL"], options?.CancelUrl ?? config["DEFAULT:CANCEL_URL"],
            options?.UserEmail, options?.DiscountCode, options?.CreatorCode, request.Id, SubscriptionPricing.ToCents(priced.First),
            Region: region, RenewalPlan: priced.RenewalPlan,
            RenewalUnitAmountCents: priced.Renewal is decimal renewal ? SubscriptionPricing.ToCents(renewal) : null,
            AccountUrl: await StripeAccountUrl(plan), TrialDays: trialDays, TrialEndsAt: DateTime.UtcNow.AddDays(trialDays ?? 0));
        var session = await FailRequestOnError(request, () => CreateStripeSubscriptionSession(stripe, checkout, vatBasisPoints), priced.Discount);
        request.SessionId = session.Id;
        request.CoinAmount = plan.Cost;
        request.ExpiresAt = priced.Discount?.ExpiresAt;
        await db.SaveChangesAsync();
        return new TopUpIdResponse { Id = session.Id, DirectLink = session.Url, TrialDays = trialDays };
    }

    /// <summary>
    /// Starts a PayPal subscription (direct sales only). Redirect the buyer to the returned approval link.
    /// </summary>
    /// <param name="userId">The user subscribing</param>
    /// <param name="productId">Slug of the subscription plan; its PayPal plan is created on the first checkout</param>
    /// <param name="options">Country, IP, urls, creator and discount code</param>
    [HttpPost]
    [Route("paypal/subscribe")]
    public async Task<TopUpIdResponse> PayPalSubscribe(string userId, string productId, [FromBody] TopUpOptions options = null)
    {
        var paypal = paypalSubscriptions ?? throw new ApiException("PayPal subscriptions are not available.");
        await subscriptionRouting.RefreshAsync();
        var user = await userService.GetOrCreate(userId);
        var plan = await GetSubscriptionPlan(productId);
        if (!PayPalPlanCatalog.IsSellable(plan))
            throw new ApiException("This plan is not available via PayPal.");
        declarations.Validate(options, plan, userId);
        var region = UsDirectSubscriptionCap.NormalizeRegion(options?.Region);
        var routing = subscriptionRouting.Capture(region);
        var country = await GetMatchingCountry(options, false, "PayPal");
        EnsureCountryAllowed(country);
        if (!routing.PayPalAvailable(country))
            throw new ApiException($"PayPal subscriptions are not available in your country ({country}).");
        var priced = await SubscriptionPrice(options, plan, SubscriptionProviders.PayPal,
            DirectVatBasisPoints(country, "PayPal"));
        var request = await AttemptBlockFraud(options, user, plan, priced.First, SubscriptionProviders.PayPal,
            RecurringIfDifferent(priced.First, priced.Following), country, region: region);
        if (request.SessionId != null)
            return await ExistingPayPalApproval(paypal, request.SessionId, plan);
        await RememberCountry(user, country, options);
        await declarations.Record(options, user, plan, request);
        var trialDays = await trials.DaysFor(options, plan, user, priced.Discount);
        var created = await FailRequestOnError(request,
            () => CreateApprovablePayPalSubscription(paypal, plan, priced, user, options, trialDays), priced.Discount);
        var approve = ApproveLink(created);
        // The webhook only activates subscriptions recorded here, for exactly this price, and not long after the code expired.
        request.SessionId = created.Id;
        request.CoinAmount = plan.Cost;
        request.ExpiresAt = priced.Discount?.ExpiresAt;
        request.CreatorCode = options?.CreatorCode;
        request.DiscountCode = priced.Discount?.Code;
        await db.SaveChangesAsync();
        return new TopUpIdResponse { Id = created.Id, DirectLink = approve, TrialDays = trialDays };
    }

    /// <param name="trialDays">A free trial first: the plan variant with a free TRIAL cycle of these days, the paid cycles follow</param>
    private async Task<Coflnet.Payments.Models.PayPal.PayPalSubscription> CreatePayPalSubscription(IPayPalSubscriptionApi paypal, TopUpProduct plan,
        SubscriptionCheckoutPrice priced, User user, TopUpOptions options, int? trialDays)
    {
        var planId = await PayPalPlans.GetOrCreate(plan, priced.RenewalPlan, trialDays);
        // The REGULAR cycles: the 4-week renewal price of 13 and 52 week plans, the plan's recurring price otherwise
        return await paypal.Create(new PayPalSubscriptionRequest(planId,
            SubscriptionService.PayPalCustomId(plan.Id, user.ExternalId), priced.First, plan.CurrencyCode.ToUpperInvariant(),
            options?.SuccessUrl ?? config["DEFAULT:SUCCESS_URL"], options?.CancelUrl ?? config["DEFAULT:CANCEL_URL"],
            options?.UserEmail, priced.Following, TrialDays: trialDays));
    }

    private SubscriptionTrials trials => subscriptionTrials ??= new SubscriptionTrials(db, config);
    private SubscriptionTrials subscriptionTrials;

    private SubscriptionDeclarationService declarations => subscriptionDeclarations ??=
        new SubscriptionDeclarationService(db, config, _logger);
    private SubscriptionDeclarationService subscriptionDeclarations;

    private SubscriptionRenewalPlans renewalPlans => subscriptionRenewalPlans ??= new SubscriptionRenewalPlans(db, config);
    private SubscriptionRenewalPlans subscriptionRenewalPlans;

    private void EnsureLemonSqueezySubscriptionAllowed(string country, string region)
    {
        if (subscriptionRouting.Capture(region).AllowsLemonSqueezyCheckout(country))
            return;
        throw new ApiException(subscriptionRouting.LemonSqueezyCheckoutEnabled
            ? "Please subscribe with one of the payment methods offered for your country."
            : "New Lemon Squeezy subscriptions are no longer available.");
    }

    /// <summary>Where the disclosure tells the buyer to cancel, by the plan's product line (needs its groups, the plan is loaded without)</summary>
    private async Task<string> StripeAccountUrl(TopUpProduct plan)
    {
        var withGroups = await db.TopUpProducts.Include(p => p.Groups).AsNoTracking().FirstOrDefaultAsync(p => p.Id == plan.Id);
        return StripeSubscriptionDisclosure.AccountUrlFor(withGroups ?? plan, config);
    }

    private async Task<TopUpProduct> GetSubscriptionPlan(string productSlug)
    {
        var plan = await productService.GetTopupProduct(productSlug);
        if (!SubscriptionService.IsSubscriptionPlan(plan))
            throw new ApiException("Product is not a subscription plan");
        return new TopUpProduct(plan);
    }

    /// <summary>
    /// Direct countries get the one-time payment checks (selected country must match the IP country) and the VAT rate
    /// added to their price; Managed Payments buyers are taxed by Stripe (rate 0 here), so their country only selects the mode.
    /// </summary>
    private async Task<(string country, StripeCheckoutMode mode, int vatBasisPoints)> StripeCountryAndMode(
        TopUpOptions options, SubscriptionRouting.Snapshot routing)
    {
        var selected = options?.Country?.Trim().ToUpperInvariant()
            ?? options?.Locale?.Split(',').FirstOrDefault()?.Split('-').LastOrDefault()?.ToUpperInvariant();
        var country = selected?.Length == 2 ? selected : await ipCountryLookup.GetCountry(options?.UserIp);
        var direct = routing.IsDirectCountry(country);
        if (direct)
        {
            country = await GetMatchingCountry(options, false, "Stripe");
            EnsureCountryAllowed(country);
        }
        var vatBasisPoints = direct ? DirectVatBasisPoints(country, "Stripe") : 0;
        var mode = routing.StripeMode(country)
            ?? throw new ApiException($"Stripe subscriptions are not available in your country ({country ?? "unknown"}).");
        if (mode == StripeCheckoutMode.Managed)
            await EnsureManagedCoversIpCountry(options?.UserIp);
        return (country, mode, vatBasisPoints);
    }

    /// <summary>
    /// The selected country of a Managed Payments buyer is not verified, so a buyer located in a country Stripe does not
    /// cover for tax, or one we block, could pick a covered one. The IP country, when known, must be covered as well.
    /// </summary>
    private async Task EnsureManagedCoversIpCountry(string userIp)
    {
        var ipCountry = await ipCountryLookup.GetCountry(userIp);
        if (ipCountry == null || subscriptionRouting.ManagedCovers(ipCountry))
            return;
        throw new ApiException($"Stripe subscriptions are not available in your country ({ipCountry}).");
    }

    /// <summary>
    /// The VAT added to the net price of a direct sale to <paramref name="country"/>. A direct country without a
    /// configured rate is refused: selling it at the net price would undercharge.
    /// </summary>
    private int DirectVatBasisPoints(string country, string provider)
    {
        if (subscriptionRouting.Tax.RateFor(country) is int basisPoints)
            return basisPoints;
        _logger.LogError("No VAT rate configured ({Key}{Country}); refusing the direct {Provider} subscription checkout",
            SubscriptionTax.RateKeyPrefix, country, provider);
        throw new ApiException($"{provider} subscriptions are not available in your country ({country}).");
    }

    /// <summary>
    /// What a subscription checkout charges: the first payment, the plan's recurring price (they differ for "first payment
    /// only" codes), for 13 and 52 week plans the 4-week renewal price, and the validated code with the amount of the plan's
    /// billing period applied.
    /// Plan prices are net: all amounts are the discounted net price plus <paramref name="vatBasisPoints"/> of VAT
    /// (0 for Managed Payments, where Stripe adds the tax), i.e. what the provider will charge.
    /// </summary>
    private async Task<SubscriptionCheckoutPrice> SubscriptionPrice(TopUpOptions options, TopUpProduct plan, string provider, int vatBasisPoints)
    {
        if ((options?.TopUpAmount ?? 0) > 0)
            throw new ApiException("Subscriptions have a fixed price.");
        var renewalPlan = await renewalPlans.Require(plan);
        // GetPriceAndCoins applies the creator code; the discount code is applied by the direct providers themselves.
        var (creatorPrice, _, creatorCode, validated) = await GetPriceAndCoins(options, plan, isSubscription: true, checkoutProvider: provider);
        var discount = validated?.ForPeriod(SubscriptionService.IntervalWeeks(plan.OwnershipSeconds));
        var net = SubscriptionPricing.Apply(creatorPrice, null, discount);
        var recurringNet = discount?.Duration == DiscountCode.FirstPaymentOnly ? creatorPrice : net;
        var price = SubscriptionPricing.GrossCents(net, vatBasisPoints) / 100m;
        var recurringPrice = SubscriptionPricing.GrossCents(recurringNet, vatBasisPoints) / 100m;
        var renewal = renewalPlan == null ? (decimal?)null
            // from the code as validated: the copy above already holds the amount of the plan's own period
            : SubscriptionPricing.GrossCents(SubscriptionPricing.RenewalNet(renewalPlan.Price, creatorCode, validated), vatBasisPoints) / 100m;
        if (price * 100 < SubscriptionPricing.MinimumCents || renewal * 100 < SubscriptionPricing.MinimumCents)
            throw new ApiException("The discounted price is below the minimum charge.");
        return new(price, recurringPrice, discount, renewalPlan, renewal);
    }

    /// <summary>
    /// The prices of a subscription checkout (see <see cref="SubscriptionPrice"/>)
    /// </summary>
    /// <param name="First">The first payment</param>
    /// <param name="Recurring">The plan's recurring price (the Stripe line item; never charged again for 13/52 week plans)</param>
    /// <param name="Discount">The validated code with the amount of the plan's period</param>
    /// <param name="RenewalPlan">13/52 week plans: the 4-week plan the subscription continues as</param>
    /// <param name="Renewal">13/52 week plans: what every 4-week renewal charges</param>
    private sealed record SubscriptionCheckoutPrice(decimal First, decimal Recurring, ValidatedDiscount Discount,
        TopUpProduct RenewalPlan, decimal? Renewal)
    {
        /// <summary>What every payment after the first charges</summary>
        public decimal Following => Renewal ?? Recurring;
    }

    private static decimal? RecurringIfDifferent(decimal price, decimal recurringPrice) =>
        recurringPrice == price ? null : recurringPrice;

    private async Task RememberCountry(User user, string country, TopUpOptions options)
    {
        if (country != null && user.Country != country)
            user.Country = country;
        if (user.Locale == null && options?.Locale != null)
            user.Locale = options.Locale;
        await db.SaveChangesAsync();
    }

    private async Task<Coflnet.Payments.Models.PayPal.PayPalSubscription> CreateApprovablePayPalSubscription(IPayPalSubscriptionApi paypal, TopUpProduct plan,
        SubscriptionCheckoutPrice priced, User user, TopUpOptions options, int? trialDays)
    {
        var created = await CreatePayPalSubscription(paypal, plan, priced, user, options, trialDays);
        if (ApproveLink(created) == null)
            throw new ApiException("PayPal did not return an approval link.");
        return created;
    }

    private async Task<Session> CreateStripeSubscriptionSession(IStripeSubscriptionGateway stripe, StripeSubscriptionCheckout checkout,
        int vatBasisPoints)
    {
        try
        {
            checkout = await WithStripeObjects(stripe, checkout, vatBasisPoints);
            return await CreateSessionWithCustomerFallback(stripe, checkout);
        }
        catch (Stripe.StripeException e) when (StripeErrorClassifier.IsCredentialError(e.HttpStatusCode))
        {
            PaymentMetrics.StripeCredentialErrors.Inc();
            _logger.LogCritical("Stripe subscription checkout was rejected by Stripe credentials (HTTP {StatusCode}, Stripe code {StripeCode})",
                e.HttpStatusCode, e.StripeError?.Code);
            throw new ApiException(StripeErrorClassifier.UserMessage);
        }
        catch (Exception e) when (e is not ApiException)
        {
            var stripeError = e as Stripe.StripeException;
            if (stripeError == null && e is not HttpRequestException)
                _logger.LogError(e, "Stripe subscription checkout session failed before reaching Stripe");
            // the parameter Stripe refused names the cause (e.g. custom_text); the message is left out, it can echo the buyer's email
            _logger.LogError("Stripe subscription checkout session could not be created ({ExceptionType}, HTTP {StatusCode}, Stripe code {StripeCode}, parameter {StripeParam})",
                e.GetType().Name, stripeError?.HttpStatusCode ?? (e as HttpRequestException)?.StatusCode, stripeError?.StripeError?.Code,
                stripeError?.StripeError?.Param);
            throw new ApiException("Stripe payment is temporarily unavailable. Please try again later.");
        }
    }

    /// <summary>
    /// Retry a Managed Payments session once with the email if Stripe responds with HTTP 400 when a customer was supplied.
    /// </summary>
    private async Task<Session> CreateSessionWithCustomerFallback(IStripeSubscriptionGateway stripe, StripeSubscriptionCheckout checkout)
    {
        var managed = StripeManagedSettings.From(config);
        try
        {
            return await stripe.CreateCheckoutSession(StripeSubscriptionCheckoutBuilder.Build(checkout, managed));
        }
        catch (Stripe.StripeException e) when (checkout.CustomerId != null && e.HttpStatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            _logger.LogWarning("Stripe refused the Managed Payments session (HTTP 400, {StripeCode}); retrying once without the customer (parameter {StripeParam})",
                e.StripeError?.Code, e.StripeError?.Param);
            return await stripe.CreateCheckoutSession(StripeSubscriptionCheckoutBuilder.Build(checkout with { CustomerId = null }, managed));
        }
    }

    /// <summary>
    /// Creates the session's one-time coupon, direct VAT rate, and Managed Payments customer as applicable.
    /// </summary>
    private async Task<StripeSubscriptionCheckout> WithStripeObjects(IStripeSubscriptionGateway stripe,
        StripeSubscriptionCheckout checkout, int vatBasisPoints)
    {
        var coupon = StripeFirstPaymentCoupon.For(checkout.Mode, checkout.UnitAmountCents,
            checkout.FirstPaymentCents ?? checkout.UnitAmountCents, checkout.Plan.CurrencyCode);
        var couponId = coupon == null ? null : await stripe.EnsureFirstPaymentCoupon(coupon);
        var taxRateId = checkout.Mode == StripeCheckoutMode.Direct && vatBasisPoints > 0
            ? await stripe.EnsureVatTaxRate(checkout.Country, vatBasisPoints) : null;
        var customerId = checkout.Mode == StripeCheckoutMode.Managed ? await CreateManagedStripeCustomer(stripe, checkout.Email) : null;
        return checkout with { CouponId = couponId, TaxRateId = taxRateId, CustomerId = customerId };
    }

    private async Task<string> CreateManagedStripeCustomer(IStripeSubscriptionGateway stripe, string email)
    {
        try
        {
            return await stripe.CreateCustomer(email, new Dictionary<string, string>
            {
                [StripeManagedCustomer.SellerKey] = StripeManagedCustomer.SellerValue
            });
        }
        catch (Exception e) when (e is Stripe.StripeException or HttpRequestException or TaskCanceledException)
        {
            var stripeError = e as Stripe.StripeException;
            var level = stripeError != null && StripeErrorClassifier.IsCredentialError(stripeError.HttpStatusCode) ? LogLevel.Error : LogLevel.Warning;
            if (level == LogLevel.Error)
                PaymentMetrics.StripeCredentialErrors.Inc(); // e.g. the restricted key lacks Customers: Write
            _logger.Log(level, "Stripe Managed Payments customer creation failed (HTTP {StatusCode}, Stripe code {StripeCode}). Continuing with email-only checkout.",
                stripeError?.HttpStatusCode ?? (e as HttpRequestException)?.StatusCode, stripeError?.StripeError?.Code);
            return null;
        }
    }

    private static async Task<TopUpIdResponse> ExistingStripeCheckout(IStripeSubscriptionGateway stripe, string sessionId)
    {
        var existing = await stripe.GetCheckoutSession(sessionId);
        if (string.IsNullOrWhiteSpace(existing.Url))
            throw new ApiException("The existing Stripe checkout session is no longer active. Please try again later.");
        return new TopUpIdResponse { Id = existing.Id, DirectLink = existing.Url, TrialDays = SessionTrialDays(existing) };
    }

    /// <summary>The trial a reused session gives (an open checkout of the same prices is reused whether or not it has a trial)</summary>
    private static int? SessionTrialDays(Session session) =>
        int.TryParse(session.Metadata?.GetValueOrDefault(SubscriptionTrials.MetadataKey), out var days) && days > 0 ? days : null;

    private async Task<TopUpIdResponse> ExistingPayPalApproval(IPayPalSubscriptionApi paypal, string subscriptionId, TopUpProduct plan)
    {
        var existing = await paypal.Get(subscriptionId);
        var approve = existing?.Status == "APPROVAL_PENDING" ? ApproveLink(existing) : null;
        if (approve == null)
            throw new ApiException("A PayPal subscription checkout is already in progress. Please try again later.");
        return new TopUpIdResponse { Id = existing.Id, DirectLink = approve, TrialDays = await PayPalPlans.TrialDays(existing.PlanId, plan) };
    }

    private static string ApproveLink(Coflnet.Payments.Models.PayPal.PayPalSubscription subscription) =>
        subscription?.Links?.FirstOrDefault(l => l.Rel == "approve")?.Href;
}
