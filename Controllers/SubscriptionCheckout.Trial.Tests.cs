using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Stripe.Checkout;

namespace Payments.Controllers;

/// <summary>Free trials of direct subscription checkouts (<see cref="SubscriptionTrials"/>)</summary>
public partial class SubscriptionCheckoutControllerTests
{
    private static readonly Dictionary<string, string> TrialsOn = new() { [SubscriptionTrials.EnabledKey] = "true" };

    private async Task<TopUpProduct> BazaarPro(string slug = "l_bazaarpro", int ownershipSeconds = FourWeeks, int slots = 0)
    {
        var plan = new TopUpProduct
        {
            Slug = slug, Title = "Bazaar Pro", Price = 12.99m, Cost = 2100, CurrencyCode = "eur", ProviderSlug = "lemonsqueezy",
            OwnershipSeconds = ownershipSeconds, Type = Product.ProductType.SERVICE, SlotCount = slots
        };
        db.TopUpProducts.Add(plan);
        await db.SaveChangesAsync();
        await groups.AddProductToGroup(plan, "premium");
        await groups.AddProductToGroup(plan, "l_bazaarpro");
        db.ChangeTracker.Clear();
        return plan;
    }

    private static TopUpOptions TrialOptions(string country, int days = 3, string discount = null)
    {
        var options = Options(country, discount);
        options.EnableTrial = true;
        options.TrialLengthDays = days;
        return options;
    }

    [Test]
    public async Task TrialsAreOffByDefault()
    {
        await BazaarPro();

        var response = await Controller("DE").StripeSubscribe("buyer", "l_bazaarpro", TrialOptions("DE"));

        Assert.That(created.SubscriptionData.TrialPeriodDays, Is.Null);
        Assert.That(created.SubscriptionData.Metadata.ContainsKey(SubscriptionTrials.MetadataKey), Is.False);
        Assert.That(response.TrialDays, Is.Null, "the request for a trial is ignored, the checkout proceeds without one");
    }

    /// <summary>
    /// BazaarPro-API asks for 3 days: Stripe gets trial_period_days (Checkout still collects the payment method) and the text next
    /// to the button names the trial, its end and the first charge
    /// </summary>
    [Test]
    public async Task BazaarProCheckoutGetsTheTrialAndTheDisclosureNamesTheFirstCharge()
    {
        await BazaarPro();

        var response = await Controller("DE", TrialsOn).StripeSubscribe("buyer", "l_bazaarpro", TrialOptions("DE"));

        Assert.That(created.SubscriptionData.TrialPeriodDays, Is.EqualTo(3));
        Assert.That(created.SubscriptionData.Metadata[SubscriptionTrials.MetadataKey], Is.EqualTo("3"));
        Assert.That(created.PaymentMethodCollection, Is.Null, "the default (always) collects the payment method");
        Assert.That(response.TrialDays, Is.EqualTo(3));
        var end = DateTime.UtcNow.AddDays(3).ToString(StripeSubscriptionDisclosure.TrialDateFormat, CultureInfo.InvariantCulture);
        Assert.That(created.CustomText.Submit.Message, Does.StartWith(
            $"Free for 3 days until {end}; cancel before then and nothing is charged. The first payment of 15.46 EUR is charged on {end}. "
            + "Renews every 4 weeks at 15.46 EUR until cancelled."));
        Assert.That(created.CustomText.Submit.Message.Length, Is.LessThanOrEqualTo(StripeSubscriptionDisclosure.MaxLength));
    }

    [Test]
    public async Task PremiumHasNoTrial()
    {
        var response = await Controller("DE", TrialsOn).StripeSubscribe("buyer", "l_premium", TrialOptions("DE"));

        Assert.That(created.SubscriptionData.TrialPeriodDays, Is.Null);
        Assert.That(response.TrialDays, Is.Null);
    }

    [Test]
    public async Task OnlyTheConfiguredLinesHaveTrials()
    {
        await BazaarPro();
        var settings = new Dictionary<string, string>(TrialsOn) { [SubscriptionTrials.LinesKey] = "default" };

        await Controller("DE", settings).StripeSubscribe("buyer", "l_bazaarpro", TrialOptions("DE"));
        Assert.That(created.SubscriptionData.TrialPeriodDays, Is.Null);

        await Controller("DE", settings).StripeSubscribe("other", "l_premium", TrialOptions("DE"));
        Assert.That(created.SubscriptionData.TrialPeriodDays, Is.EqualTo(3), "a configured line gets trials");
    }

    [TestCase(30, null, 7)]
    [TestCase(30, "5", 5)]
    [TestCase(0, null, null)]
    public async Task TheTrialIsCappedAtTheMaximumLength(int requested, string maxDays, int? expected)
    {
        await BazaarPro();
        var settings = new Dictionary<string, string>(TrialsOn);
        if (maxDays != null)
            settings[SubscriptionTrials.MaxDaysKey] = maxDays;

        var response = await Controller("DE", settings).StripeSubscribe("buyer", "l_bazaarpro", TrialOptions("DE", requested));

        Assert.That(created.SubscriptionData.TrialPeriodDays, Is.EqualTo(expected));
        Assert.That(response.TrialDays, Is.EqualTo(expected));
    }

    /// <summary>One trial per buyer and line: an earlier (Lemon Squeezy) trial, subscription or ownership of any BazaarPro plan counts</summary>
    [TestCase("trial")]
    [TestCase("subscription")]
    [TestCase("ownership")]
    public async Task ABuyerWhoUsedTheLineBeforeGetsNoTrial(string earlier)
    {
        await BazaarPro();
        var year = await BazaarPro("l_bazaarpro-year", 31536000);
        var buyer = await users.GetOrCreate("buyer");
        var product = await db.TopUpProducts.SingleAsync(p => p.Id == year.Id);
        if (earlier == "trial")
            db.TrialUsages.Add(new TrialUsage { UserId = buyer.Id, ProductId = year.Id, TrialStartedAt = DateTime.UtcNow.AddYears(-1) });
        if (earlier == "subscription")
            db.Subscriptions.Add(new UserSubscription { User = buyer, Product = product, ProviderSlug = "lemonsqueezy", ExternalId = "ls_1", Status = "expired" });
        if (earlier == "ownership")
            db.OwnerShips.Add(new OwnerShip { User = buyer, Product = product, Expires = DateTime.UtcNow.AddDays(-100) });
        await db.SaveChangesAsync();

        var response = await Controller("DE", TrialsOn).StripeSubscribe("buyer", "l_bazaarpro", TrialOptions("DE"));

        Assert.That(created.SubscriptionData.TrialPeriodDays, Is.Null);
        Assert.That(response.TrialDays, Is.Null);
    }

    [Test]
    public async Task APremiumSubscriberStillGetsABazaarProTrial()
    {
        await BazaarPro();
        var buyer = await users.GetOrCreate("buyer");
        db.OwnerShips.Add(new OwnerShip { User = buyer, Product = await db.TopUpProducts.SingleAsync(p => p.Slug == "l_premium"), Expires = DateTime.UtcNow.AddDays(10) });
        await db.SaveChangesAsync();

        await Controller("DE", TrialsOn).StripeSubscribe("buyer", "l_bazaarpro", TrialOptions("DE"));

        Assert.That(created.SubscriptionData.TrialPeriodDays, Is.EqualTo(3));
    }

    /// <summary>
    /// A "first payment only" code is a one-time coupon at Stripe, which the trial's zero invoice would use up (a duration=once coupon
    /// is used by the first finalized invoice): such a checkout gets the discount and no trial. Codes on every payment keep the trial.
    /// </summary>
    [TestCase(DiscountCode.FirstPaymentOnly, null)]
    [TestCase("forever", 3)]
    public async Task AFirstPaymentOnlyCodeReplacesTheTrial(string duration, int? expected)
    {
        await BazaarPro();
        db.DiscountCodes.Add(new DiscountCode { Code = "GTA6", AmountType = "percent", Amount = 25, SubscriptionOnly = true, Duration = duration, IsActive = true });
        await db.SaveChangesAsync();

        var response = await Controller("DE", TrialsOn).StripeSubscribe("buyer", "l_bazaarpro", TrialOptions("DE", discount: "GTA6"));

        Assert.That(created.SubscriptionData.TrialPeriodDays, Is.EqualTo(expected));
        Assert.That(response.TrialDays, Is.EqualTo(expected));
        Assert.That(created.Discounts?.Count ?? 0, Is.EqualTo(expected == null ? 1 : 0));
    }

    /// <summary>Managed Payments supports trials (Stripe sends its own trial start email there); trial_period_days is allowed</summary>
    [Test]
    public async Task ManagedPaymentsCheckoutsGetTheTrialToo()
    {
        await BazaarPro();

        await Controller("NO", TrialsOn).StripeSubscribe("buyer", "l_bazaarpro", TrialOptions("NO"));

        Assert.That(created.ManagedPayments?.Enabled, Is.True);
        Assert.That(created.SubscriptionData.TrialPeriodDays, Is.EqualTo(3));
        Assert.That(created.CustomText.Submit.Message, Does.StartWith("Free for 3 days until "));
    }

    [Test]
    public async Task SlotBundlesHaveNoTrial()
    {
        await BazaarPro("l_bazaarpro-slots-4", slots: 4);

        await Controller("DE", TrialsOn).StripeSubscribe("buyer", "l_bazaarpro-slots-4", TrialOptions("DE"));

        Assert.That(created.SubscriptionData.TrialPeriodDays, Is.Null);
    }

    [Test]
    public async Task AReusedOpenCheckoutReportsItsOwnTrial()
    {
        await BazaarPro();
        var controller = Controller("DE", TrialsOn);
        await controller.StripeSubscribe("buyer", "l_bazaarpro", TrialOptions("DE"));
        stripe.Setup(s => s.GetCheckoutSession("cs_1")).ReturnsAsync(new Session
        {
            Id = "cs_1", Url = "https://checkout.stripe.com/c/cs_1", Metadata = new Dictionary<string, string>(created.Metadata)
        });

        var again = await controller.StripeSubscribe("buyer", "l_bazaarpro", Options("DE"));

        Assert.That(again.Id, Is.EqualTo("cs_1"));
        Assert.That(again.TrialDays, Is.EqualTo(3));
    }
}
