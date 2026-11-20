using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Stripe;

namespace Coflnet.Payments.Services.Subscriptions;

/// <summary>
/// Regression: Stripe's pending updates (pending_if_incomplete, used by term switches and upgrades) accept only price, quantity and
/// discounts on items, not price_data, so both name a real Price
/// </summary>
public class StripePlanPricesTests
{
    private static readonly StripeSubscriptionSnapshot Current = new("sub_1", "active", false, null, null, null, "cus_1",
        new Dictionary<string, string>(), "si_1", "price_old", "prod_checkout", 1153, "eur", null, false, default);

    private static readonly StripePlanPriceSpec Year = new("l_premium-year", "52 weeks premium", 5931, "eur", 52, null);

    [Test]
    public void TermSwitchNamesThePriceAndStartsTheTermNow()
    {
        var options = StripePlanPrices.TermSwitchOptions(Current, "price_year");

        Assert.That((options.Items.Count, options.Items[0].Id, options.Items[0].Price, options.Items[0].PriceData),
            Is.EqualTo((1, "si_1", "price_year", (SubscriptionItemPriceDataOptions)null)));
        Assert.That((options.PaymentBehavior, options.ProrationBehavior), Is.EqualTo(("pending_if_incomplete", "none")));
        Assert.That(options.BillingCycleAnchor, Is.EqualTo(SubscriptionBillingCycleAnchor.Now));
    }

    [TestCase(true, "pending_if_incomplete", "always_invoice")]
    [TestCase(false, "allow_incomplete", "none")]
    public void PlanChangeNamesThePrice(bool upgrade, string paymentBehavior, string proration)
    {
        var options = StripePlanPrices.PlanChangeOptions(Current, "price_plus", upgrade);

        Assert.That((options.Items[0].Price, options.Items[0].PriceData), Is.EqualTo(("price_plus", (SubscriptionItemPriceDataOptions)null)));
        Assert.That((options.PaymentBehavior, options.ProrationBehavior), Is.EqualTo((paymentBehavior, proration)));
    }

    [Test]
    public void PriceIsCreatedWithTheTermsAndAStableKey()
    {
        var options = StripePlanPrices.CreateOptions(Year, "coflnet_plan_l_premium-year");

        Assert.That((options.Product, options.UnitAmount, options.Currency, options.Recurring.Interval, options.Recurring.IntervalCount),
            Is.EqualTo(("coflnet_plan_l_premium-year", (long?)5931, "eur", "week", (long?)52)));
        Assert.That(options.LookupKey, Is.EqualTo("coflnet_l_premium-year_52w_5931_eur_unspecified"));
        Assert.That(StripePlanPrices.LookupKey(Year with { TaxBehavior = "exclusive" }), Is.Not.EqualTo(options.LookupKey));
        Assert.That((options.TransferLookupKey, StripePlanPrices.CreateOptions(Year, "p", transferLookupKey: true).TransferLookupKey),
            Is.EqualTo(((bool?)null, (bool?)true)));
    }

    [Test]
    public void OnlyAnIdenticalPriceIsReused()
    {
        Price Price(long amount = 5931, long weeks = 52, string product = "coflnet_plan_l_premium-year", bool active = true) => new()
        {
            Active = active, ProductId = product, UnitAmount = amount, Currency = "eur", TaxBehavior = "unspecified",
            Recurring = new PriceRecurring { Interval = "week", IntervalCount = weeks }
        };
        const string product = "coflnet_plan_l_premium-year";

        Assert.That(StripePlanPrices.Matches(Price(), product, Year), Is.True);
        Assert.That(StripePlanPrices.Matches(Price(amount: 5932), product, Year), Is.False);
        Assert.That(StripePlanPrices.Matches(Price(weeks: 4), product, Year), Is.False);
        Assert.That(StripePlanPrices.Matches(Price(product: "prod_other"), product, Year), Is.False);
        Assert.That(StripePlanPrices.Matches(Price(active: false), product, Year), Is.False);
        Assert.That(StripePlanPrices.Matches(Price(), product, Year with { TaxBehavior = "exclusive" }), Is.False);
    }

    /// <summary>A cached price someone archived is evicted and the update retried once with a fresh price</summary>
    [Test]
    public async Task ArchivedPriceIsLookedUpAgainOnce()
    {
        var (ensured, evicted, prices) = (0, 0, new List<string>());
        var archived = new StripeException(System.Net.HttpStatusCode.BadRequest,
            new StripeError { Param = "items[0][price]", Message = "The price specified is inactive." }, "inactive");

        var result = await StripePlanPrices.WithUsablePrice(() => System.Threading.Tasks.Task.FromResult("price_" + ++ensured), () => evicted++,
            price =>
            {
                prices.Add(price);
                return price == "price_1" ? System.Threading.Tasks.Task.FromException<string>(archived) : System.Threading.Tasks.Task.FromResult("updated");
            });

        Assert.That((result, ensured, evicted), Is.EqualTo(("updated", 2, 1)));
        Assert.That(prices, Is.EqualTo(new[] { "price_1", "price_2" }));
    }

    [Test]
    public void OtherStripeErrorsAreNotRetried()
    {
        var declined = new StripeException(System.Net.HttpStatusCode.PaymentRequired, new StripeError { Type = "card_error", Message = "declined" }, "declined");
        var evicted = 0;

        Assert.ThrowsAsync<StripeException>(() => StripePlanPrices.WithUsablePrice(() => System.Threading.Tasks.Task.FromResult("price_1"),
            () => evicted++, _ => System.Threading.Tasks.Task.FromException<string>(declined)));
        Assert.That(evicted, Is.Zero);
    }
}
