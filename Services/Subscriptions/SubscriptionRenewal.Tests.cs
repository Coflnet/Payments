using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Models.LemonSqueezy;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Keys = Coflnet.Payments.Services.SubscriptionService.StripeMetadata;

namespace Coflnet.Payments.Services.Subscriptions;

/// <summary>
/// Owner decision (§ 309 Nr. 9 b BGB): after the first 13 or 52 weeks a subscription continues as the 4-week plan of the
/// same tier at its 4-week price until cancelled.
/// </summary>
public class SubscriptionRenewalTests
{
    private const long FourWeeks = 2430000;
    private const long ThirteenWeeks = 7776000;
    private const long Year = 31536000;
    private int nextId = 1;

    private TopUpProduct Plan(string slug, long seconds, decimal price, string tier = "premium", int slots = 0, string currency = "eur")
    {
        var plan = new TopUpProduct
        {
            Id = nextId++, Slug = slug, Title = slug, Price = price, CurrencyCode = currency, OwnershipSeconds = seconds,
            Type = Product.ProductType.SERVICE, SlotCount = slots, SlotTier = slots > 0 ? tier : null, Groups = []
        };
        if (slots == 0)
            plan.Groups.Add(new Group { Slug = tier });
        return plan;
    }

    [TestCase(FourWeeks, false)]
    [TestCase(ThirteenWeeks, true)]
    [TestCase(Year, true)]
    [TestCase(86400L, false)]
    public void OnlyThirteenAndFiftyTwoWeekPlansContinueAsFourWeeks(long seconds, bool continues) =>
        Assert.That(SubscriptionRenewal.ContinuesAsFourWeeks(Plan("p", seconds, 1)), Is.EqualTo(continues));

    [Test]
    public void TheFourWeekPlanOfTheSameTierIsChosen()
    {
        var year = Plan("l_prem_plus-year", Year, 369.69m, "premium_plus");
        var candidates = new[]
        {
            Plan("l_premium", FourWeeks, 9.69m), Plan("l_prem_plus", FourWeeks, 27.69m, "premium_plus"),
            Plan("l_prem_plus-quarter", ThirteenWeeks, 79.69m, "premium_plus"), year
        };
        Assert.That(SubscriptionRenewal.Select(year, candidates, null)?.Slug, Is.EqualTo("l_prem_plus"));
    }

    [Test]
    public void SlotBundlesContinueAsTheFourWeekBundleOfTheSameSizeAndTier()
    {
        var year = Plan("l_premium-slots-4-year", Year, 300m, "premium", slots: 4);
        var candidates = new[]
        {
            Plan("l_premium", FourWeeks, 9.69m), Plan("l_premium-slots-3", FourWeeks, 25m, "premium", 3),
            Plan("l_premium-slots-4", FourWeeks, 33.69m, "premium", 4), Plan("l_prem_plus-slots-4", FourWeeks, 90m, "premium_plus", 4)
        };
        Assert.That(SubscriptionRenewal.Select(year, candidates, null)?.Slug, Is.EqualTo("l_premium-slots-4"));
    }

    [Test]
    public void SeveralMatchingPlansAreDecidedByTheSlugTheLongPlanStartsWith()
    {
        var year = Plan("l_premium-year", Year, 99.69m);
        var candidates = new[] { Plan("premium-4w-legacy", FourWeeks, 8m), Plan("l_premium", FourWeeks, 9.69m) };
        Assert.That(SubscriptionRenewal.Select(year, candidates, null)?.Slug, Is.EqualTo("l_premium"));
        var unrelated = Plan("premium_year", Year, 99.69m);
        Assert.That(SubscriptionRenewal.Select(unrelated, candidates, null), Is.Null, "ambiguous: never guessed");
    }

    [Test]
    public void ConfiguredRenewalPlanWinsButOnlyWhenItIsAValidRenewalPlan()
    {
        var year = Plan("l_premium-year", Year, 99.69m);
        var candidates = new[] { Plan("premium-4w-legacy", FourWeeks, 8m), Plan("l_premium", FourWeeks, 9.69m), Plan("l_prem_plus", FourWeeks, 27.69m, "premium_plus") };
        Assert.That(SubscriptionRenewal.Select(year, candidates, "premium-4w-legacy")?.Slug, Is.EqualTo("premium-4w-legacy"));
        Assert.That(SubscriptionRenewal.Select(year, candidates, "l_prem_plus"), Is.Null, "another tier is never a renewal plan");
    }

    [Test]
    public void DisabledFreeOrOtherCurrencyPlansAreNoRenewalPlans()
    {
        var year = Plan("l_premium-year", Year, 99.69m);
        var disabled = Plan("l_premium", FourWeeks, 9.69m);
        disabled.Type |= Product.ProductType.DISABLED;
        Assert.That(SubscriptionRenewal.Select(year, [disabled, Plan("l_premium-free", FourWeeks, 0m), Plan("l_premium-usd", FourWeeks, 9.69m, currency: "usd")], null),
            Is.Null);
    }

    private static ValidatedDiscount Code(string duration, decimal amount, Dictionary<int, decimal> periods = null, string type = "percent") => new()
    {
        Code = "C", AmountType = type, Amount = amount, PeriodAmounts = periods, Duration = duration, IsValid = true
    };

    /// <summary>The creator code lasts, a "forever" code applies with its 4-week amount: 27.69 - 10% = 24.92, then -25% = 18.69</summary>
    [Test]
    public void RenewalKeepsTheCreatorCodeAndTheFourWeekAmountOfAForeverCode()
    {
        var creator = new CreatorCode { Code = "CREATOR", DiscountPercent = 10 };
        var code = Code(DiscountCode.EveryRenewal, 10, new() { [4] = 25, [13] = 50, [52] = 50 }).ForPeriod(52);
        Assert.That(SubscriptionPricing.RenewalNet(27.69m, creator, code), Is.EqualTo(18.69m));
    }

    [Test]
    public void ForeverCodeWithoutAFourWeekAmountUsesItsDefaultAmount() =>
        Assert.That(SubscriptionPricing.RenewalNet(9.69m, null, Code(DiscountCode.EveryRenewal, 10, new() { [52] = 50 })), Is.EqualTo(8.72m));

    [Test]
    public void FixedForeverCodeAppliesItsFourWeekAmount() =>
        Assert.That(SubscriptionPricing.RenewalNet(9.69m, null, Code(DiscountCode.EveryRenewal, 500, new() { [4] = 100 }, "fixed")), Is.EqualTo(8.69m));

    [Test]
    public void FirstPaymentOnlyCodeDoesNotApplyToRenewals() =>
        Assert.That(SubscriptionPricing.RenewalNet(9.69m, null, Code(DiscountCode.FirstPaymentOnly, 50)), Is.EqualTo(9.69m));

    private StripeSubscriptionCheckout YearCheckout(StripeCheckoutMode mode, long unit, long renewal, string taxRate = null) =>
        new(new User { ExternalId = "buyer" }, Plan("l_premium-year", Year, 99.69m), unit, 52, mode, "DE", "https://ok", "https://cancel",
            "a@b.c", "BF2026", "creator", 42, TaxRateId: taxRate, RenewalPlan: Plan("l_premium", FourWeeks, 9.69m), RenewalUnitAmountCents: renewal);

    private static Stripe.Checkout.SessionCreateOptions Build(StripeSubscriptionCheckout checkout) =>
        StripeSubscriptionCheckoutBuilder.Build(checkout, StripeManagedSettings.From(new ConfigurationBuilder().Build()));

    [Test]
    public void CheckoutOfAYearStatesThatItContinuesEveryFourWeeksAtTheRenewalPrice()
    {
        var direct = Build(YearCheckout(StripeCheckoutMode.Direct, 5931, 865, "txr_de")).CustomText.Submit.Message;
        var managed = Build(YearCheckout(StripeCheckoutMode.Managed, 4984, 727)).CustomText.Submit.Message;

        Assert.That(direct, Is.EqualTo("After the first 52 weeks it continues every 4 weeks at 8.65 EUR until cancelled. "
            + "Cancel anytime in your account at sky.coflnet.com/account. The price includes VAT."));
        Assert.That(managed, Does.StartWith("After the first 52 weeks it continues every 4 weeks at 7.27 EUR until cancelled."));
        Assert.That(managed, Does.Contain("before taxes"));
        Assert.That(new[] { direct.Length, managed.Length }, Is.All.LessThan(StripeSubscriptionDisclosure.MaxLength));
    }

    [Test]
    public void CheckoutOfAYearNamesTheRenewalPlanAndPriceInTheMetadata()
    {
        var options = Build(YearCheckout(StripeCheckoutMode.Direct, 5931, 865, "txr_de"));
        foreach (var metadata in new[] { options.Metadata, options.SubscriptionData.Metadata })
        {
            Assert.That(metadata[Keys.RenewalProductSlug], Is.EqualTo("l_premium"));
            Assert.That(metadata[Keys.RenewalPriceCents], Is.EqualTo("865"));
            Assert.That(metadata[Keys.PriceCents], Is.EqualTo("5931"), "the year's price (Stripe's line item)");
        }
        Assert.That(options.LineItems.Single().PriceData.Recurring.IntervalCount, Is.EqualTo(52));
    }

    /// <summary>
    /// Stripe's checkout shows the line item as "per 52 weeks"; its description states the 4-week renewal next to it
    /// (product_data.description is not on Managed Payments' disallowed list)
    /// </summary>
    [TestCase(StripeCheckoutMode.Direct, 865L, "8.65 EUR")]
    [TestCase(StripeCheckoutMode.Managed, 727L, "7.27 EUR")]
    public void YearLineItemDescriptionStatesTheFourWeekRenewal(StripeCheckoutMode mode, long renewal, string price)
    {
        var product = Build(YearCheckout(mode, 5931, renewal)).LineItems.Single().PriceData.ProductData;
        Assert.That(product.Description, Is.EqualTo($"First 52 weeks, then every 4 weeks at {price} until cancelled."));
    }

    [Test]
    public void FourWeekCheckoutHasNoRenewalTerms()
    {
        var checkout = YearCheckout(StripeCheckoutMode.Direct, 1153, 0) with
            { Plan = Plan("l_premium", FourWeeks, 9.69m), IntervalWeeks = 4, RenewalPlan = null, RenewalUnitAmountCents = null };
        var options = Build(checkout);
        Assert.That(options.CustomText.Submit.Message, Does.StartWith("Renews every 4 weeks at 11.53 EUR until cancelled."));
        Assert.That(options.LineItems.Single().PriceData.ProductData.Description, Is.Null, "the plan's own (empty) description");
        Assert.That(options.Metadata.ContainsKey(Keys.RenewalProductSlug), Is.False);
    }

    private SubscriptionPaymentOutcome Outcome(TopUpProduct product, UserSubscription subscription, string currency, bool renewal) =>
        new(subscription, product, "in_1", 1, currency, "DE", null, null, null, DateTime.UtcNow, IsRenewal: renewal);

    [Test]
    public void EventOfAYearsFirstPaymentNamesTheFourWeekRenewal()
    {
        var year = Plan("l_premium-year", Year, 99.69m);
        var subscription = new UserSubscription { Product = year, RenewalProduct = Plan("l_premium", FourWeeks, 9.69m),
            RenewalPriceCents = 1153, PriceCents = 11863, Currency = "EUR" };
        var payment = new PaymentEvent();

        SubscriptionEventTerms.Apply(payment, Outcome(year, subscription, "EUR", false));

        Assert.That((payment.SubscriptionBillingWeeks, payment.SubscriptionRenewalWeeks, payment.SubscriptionRenewalAmount, payment.SubscriptionIsRenewal),
            Is.EqualTo(((int?)52, (int?)4, (decimal?)11.53m, (bool?)false)));
    }

    /// <summary>Managed Payments: the agreed net price, but only when the buyer paid in it (not after Adaptive Pricing)</summary>
    [TestCase("EUR", 7.27)]
    [TestCase("BRL", null)]
    public void EventOfAManagedRenewalCarriesTheNetPriceInTheAgreedCurrencyOnly(string currency, double? amount)
    {
        var fourWeeks = Plan("l_premium", FourWeeks, 9.69m);
        var subscription = new UserSubscription { Product = fourWeeks, PriceCents = 727, Currency = "EUR", ManagedPayments = true };
        var payment = new PaymentEvent();

        SubscriptionEventTerms.Apply(payment, Outcome(fourWeeks, subscription, currency, true));

        Assert.That((payment.SubscriptionBillingWeeks, payment.SubscriptionRenewalWeeks, payment.SubscriptionIsRenewal), Is.EqualTo(((int?)4, (int?)4, (bool?)true)));
        Assert.That(payment.SubscriptionRenewalAmount, Is.EqualTo((decimal?)amount));
    }

    private static readonly DateTime TermStart = new(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime TermEnd = TermStart.AddDays(364);

    private static Stripe.SubscriptionSchedulePhase CurrentPhase(bool taxRate = true) => new()
    {
        StartDate = TermStart, EndDate = TermEnd,
        Items = [new Stripe.SubscriptionSchedulePhaseItem { PriceId = "price_year", Quantity = 1,
            TaxRates = taxRate ? [new Stripe.TaxRate { Id = "txr_de" }] : [] }],
        Discounts = []
    };

    private static StripeRenewalRequest Request(string taxBehavior = null, string taxRate = "txr_de") =>
        new(865, "eur", taxBehavior, "l_premium", "4 weeks premium", TermEnd, taxRate);

    /// <summary>
    /// The schedule keeps the paid year untouched (same price, tax rate, start and end) and then bills the 4-week renewal price
    /// on the plan's product from the end of the year, without prorations, and releases the subscription afterwards.
    /// </summary>
    [Test]
    public async Task ScheduleKeepsTheTermAndThenBillsTheFourWeekPrice()
    {
        var body = await SentToStripe(StripeRenewalSchedule.Update(CurrentPhase(), Request(), "coflnet_plan_l_premium"));

        Assert.That(body, Does.Contain($"phases[0][start_date]={new DateTimeOffset(TermStart).ToUnixTimeSeconds()}"));
        Assert.That(body, Does.Contain($"phases[0][end_date]={new DateTimeOffset(TermEnd).ToUnixTimeSeconds()}"));
        Assert.That(body, Does.Contain("phases[0][items][0][price]=price_year"));
        Assert.That(body, Does.Contain("phases[0][items][0][tax_rates][0]=txr_de"));
        Assert.That(body, Does.Contain("phases[1][items][0][price_data][product]=coflnet_plan_l_premium"));
        Assert.That(body, Does.Contain("phases[1][items][0][price_data][unit_amount]=865"));
        Assert.That(body, Does.Contain("phases[1][items][0][price_data][currency]=eur"));
        Assert.That(body, Does.Contain("phases[1][items][0][price_data][recurring][interval]=week"));
        Assert.That(body, Does.Contain("phases[1][items][0][price_data][recurring][interval_count]=4"));
        Assert.That(body, Does.Contain("phases[1][items][0][tax_rates][0]=txr_de"));
        Assert.That(body, Does.Contain("phases[1][billing_cycle_anchor]=phase_start"));
        Assert.That(body, Does.Contain("phases[1][proration_behavior]=none"));
        Assert.That(body, Does.Contain("phases[1][metadata][priceCents]=865"));
        Assert.That(body, Does.Contain("end_behavior=release"));
        Assert.That(body, Does.Contain("proration_behavior=none"));
        Assert.That(body, Does.Contain("metadata[coflnet_renewal]=l_premium:865"));
        Assert.That(body, Does.Not.Contain("price_data][tax_behavior]"), "direct: the amount is final, as on the checkout");
        Assert.That(body, Does.Not.Contain("phases[2]"));
        Assert.That(body, Does.Not.Contain("discounts"), "the first term's one-time coupon is used up; nothing is unset or added");
    }

    [Test]
    public async Task ManagedPaymentsScheduleKeepsTheNetPriceExclusiveOfTaxAndHasNoTaxRate()
    {
        var body = await SentToStripe(StripeRenewalSchedule.Update(CurrentPhase(taxRate: false), Request("exclusive", null), "coflnet_plan_l_premium"));
        Assert.That(body, Does.Contain("phases[1][items][0][price_data][tax_behavior]=exclusive"));
        Assert.That(body, Does.Not.Contain("tax_rates"), "Managed Payments: Stripe taxes, no tax rate is sent (not even an empty one)");
    }

    /// <summary>Regression: coupon, discount and promotion code were sent together; Stripe takes exactly one</summary>
    [Test]
    public async Task AKeptDiscountIsSentAsExactlyOneReferencePreferringTheDiscount()
    {
        var phase = CurrentPhase();
        phase.Discounts = [new Stripe.SubscriptionSchedulePhaseDiscount { CouponId = "co_1", DiscountId = "di_1", PromotionCodeId = "promo_1" }];
        var body = await SentToStripe(StripeRenewalSchedule.Update(phase, Request(), "coflnet_plan_l_premium"));

        Assert.That(body, Does.Contain("phases[0][discounts][0][discount]=di_1"));
        Assert.That(body, Does.Not.Contain("[coupon]").And.Not.Contain("[promotion_code]"));
    }

    private static Stripe.SubscriptionSchedule FreshSchedule() => new()
        { Id = "sub_sched_1", Status = "active", Metadata = new(), Phases = [CurrentPhase()] };

    private static StripeSubscriptionSnapshot Subscription(string scheduleId = null) =>
        new("sub_1", "active", false, null, TermEnd, null, "cus_1", new Dictionary<string, string>(), "si_1", "price_year",
            "prod_checkout", 11863, "eur", null, false, TermStart, false, scheduleId);

    /// <summary>
    /// Regression: when completing a schedule failed after from_subscription, the one phase schedule stayed attached and Stripe
    /// refused the buyer's cancellation. A schedule created by the attempt is released again.
    /// </summary>
    [TestCase(true, false)]
    [TestCase(false, true)]
    public void AScheduleCreatedForAFailedAttemptIsReleasedAgain(bool updateFails, bool productFails)
    {
        var stripe = new FakeScheduleCalls(FreshSchedule()) { UpdateFails = updateFails, ProductFails = productFails };
        Assert.ThrowsAsync<Stripe.StripeException>(() =>
            StripeRenewalSchedule.Schedule(Subscription(), Request(), stripe, TermStart.AddDays(1)));
        Assert.That(stripe.Released, Is.EqualTo(new[] { "sub_sched_1" }));
    }

    [Test]
    public void AnAttachedScheduleIsNotReleasedWhenItsUpdateFails()
    {
        var stripe = new FakeScheduleCalls(FreshSchedule()) { UpdateFails = true };
        Assert.ThrowsAsync<Stripe.StripeException>(() =>
            StripeRenewalSchedule.Schedule(Subscription("sub_sched_1"), Request(), stripe, TermStart.AddDays(1)));
        Assert.That(stripe.Released, Is.Empty);
    }

    [Test]
    public async Task ASuccessfulAttemptCreatesAndUpdatesOnce()
    {
        var stripe = new FakeScheduleCalls(FreshSchedule());
        Assert.That(await StripeRenewalSchedule.Schedule(Subscription(), Request(), stripe, TermStart.AddDays(1)), Is.EqualTo("sub_sched_1"));
        Assert.That((stripe.Created, stripe.Updates, stripe.Released.Count), Is.EqualTo((1, 1, 0)));
    }

    private sealed class FakeScheduleCalls(Stripe.SubscriptionSchedule schedule) : IStripeScheduleCalls
    {
        public bool UpdateFails { get; init; }
        public bool ProductFails { get; init; }
        public int Created { get; private set; }
        public int Updates { get; private set; }
        public List<string> Released { get; } = [];

        public Task<Stripe.SubscriptionSchedule> CreateFrom(string subscriptionId)
        {
            Created++;
            return Task.FromResult(schedule);
        }

        public Task<Stripe.SubscriptionSchedule> Get(string scheduleId) => Task.FromResult(schedule);

        public Task<Stripe.SubscriptionSchedule> Update(string scheduleId, Stripe.SubscriptionScheduleUpdateOptions options)
        {
            Updates++;
            return UpdateFails ? throw new Stripe.StripeException("update failed") : Task.FromResult(schedule);
        }

        public Task Release(string scheduleId)
        {
            Released.Add(scheduleId);
            return Task.CompletedTask;
        }

        public Task<string> EnsurePlanProduct(string planSlug, string planName) =>
            ProductFails ? throw new Stripe.StripeException("product failed") : Task.FromResult("coflnet_plan_" + planSlug);
    }

    [Test]
    public void AScheduleMadeForTheSameRenewalIsNotUpdatedAgain()
    {
        var schedule = new Stripe.SubscriptionSchedule
        {
            Status = "active", Metadata = new() { [StripeRenewalSchedule.MarkerKey] = "l_premium:865" }, Phases = [CurrentPhase(), new()]
        };
        Assert.That(StripeRenewalSchedule.IsDoneFor(schedule, Request()), Is.True);
        Assert.That(StripeRenewalSchedule.IsDoneFor(schedule, Request() with { UnitAmount = 900 }), Is.False, "repriced (plan switch)");
    }

    [Test]
    public void AForeignScheduleWithPlannedChangesIsNeverOverwritten()
    {
        var foreign = new Stripe.SubscriptionSchedule { Metadata = new(), Phases = [CurrentPhase(), new() { StartDate = TermEnd, EndDate = TermEnd.AddDays(30) }] };
        var fresh = new Stripe.SubscriptionSchedule { Metadata = new(), Phases = [CurrentPhase()] };
        Assert.That(StripeRenewalSchedule.HasForeignPhases(foreign, TermStart.AddDays(1)), Is.True);
        Assert.That(StripeRenewalSchedule.HasForeignPhases(fresh, TermStart.AddDays(1)), Is.False, "from_subscription: only the current phase");
    }

    /// <summary>The form body Stripe.net sends for a schedule update</summary>
    private static async Task<string> SentToStripe(Stripe.SubscriptionScheduleUpdateOptions options)
    {
        var handler = new CapturingHandler();
        var client = new Stripe.StripeClient("sk_test_123", httpClient: new Stripe.SystemNetHttpClient(new HttpClient(handler)));
        await new Stripe.SubscriptionScheduleService(client).UpdateAsync("sub_sched_1", options);
        return Uri.UnescapeDataString(handler.Body);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"sub_sched_1","object":"subscription_schedule"}""", Encoding.UTF8, "application/json")
            };
        }
    }
}
