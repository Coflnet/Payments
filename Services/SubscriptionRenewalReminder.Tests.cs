using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Coflnet.Payments.Services;

/// <summary>
/// 35 days before a 13 or 52 week term ends that continues every 4 weeks, the buyer gets one reminder per term end (with the
/// price of one more term of the same length); it is recorded as sent only after it was published.
/// </summary>
public class SubscriptionRenewalReminderTests : DirectSubscriptionTestBase
{
    private const long YearSeconds = 31536000;
    private TopUpProduct premiumYear;
    private FakeProducer producer;

    [SetUp]
    public async Task SetUpYearPlan()
    {
        EnableExtensions = true;
        premiumYear = new TopUpProduct
        {
            Slug = "l_premium-year", Title = "52 weeks premium", Price = 99.69m, Cost = 23400, CurrencyCode = "eur",
            ProviderSlug = "lemonsqueezy", OwnershipSeconds = YearSeconds, Type = Product.ProductType.SERVICE
        };
        db.TopUpProducts.Add(premiumYear);
        await db.SaveChangesAsync();
        await groups.AddProductToGroup(premiumYear, "premium");
        producer = new FakeProducer();
    }

    private static DateTime InDays(double days)
    {
        var time = DateTime.UtcNow.AddDays(days);
        return new DateTime(time.Ticks - time.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }

    /// <summary>A paid year of a direct subscription that continues as the 4-week plan at 11.53</summary>
    private async Task<UserSubscription> Year(string id, DateTime renewsAt, string provider = "stripe", string status = "active",
        bool paid = true, bool managed = false)
    {
        var subscription = new UserSubscription
        {
            User = await users.GetOrCreate("buyer"), Product = premiumYear, RenewalProduct = premium, ProviderSlug = provider,
            ExternalId = id, Status = status, RenewsAt = renewsAt, CheckoutCountry = "DE", Currency = "EUR", ManagedPayments = managed,
            PriceCents = managed ? 9969 : 11863, RenewalPriceCents = managed ? 969 : 1153, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync();
        if (paid)
            await Service().GrantPaidPeriod("buyer", premiumYear, id, "stripe-invoice-in_" + id);
        return subscription;
    }

    private Task<int> Send() => Service().SendRenewalReminders(producer, TimeSpan.FromSeconds(5));

    private async Task<List<string>> Due() =>
        (await Service().DueRenewalReminders(DateTime.UtcNow)).Select(s => s.ExternalId).ToList();

    [Test]
    public async Task OnlyPaidActiveDirectYearsEndingWithin35DaysAreDue()
    {
        await Year("sub_due", InDays(20));
        await Year("sub_later", InDays(40));
        await Year("sub_past", InDays(-1));
        await Year("sub_cancelled", InDays(20), status: "cancelled");
        await Year("sub_unpaid", InDays(20), paid: false);
        await Year("ls_1", InDays(20), provider: "lemonsqueezy");
        await Year("I-DUE", InDays(10), provider: "paypal");
        var fourWeeks = await Year("sub_4w", InDays(20));
        fourWeeks.Product = premium;
        fourWeeks.RenewalProduct = null;
        await db.SaveChangesAsync();

        Assert.That(await Due(), Is.EqualTo(new[] { "I-DUE", "sub_due" }));
    }

    [Test]
    public async Task ExtendedTermsAreNotRemindedOf()
    {
        var extended = await Year("sub_extended", InDays(20));
        extended.ExtensionStartsAt = extended.RenewsAt;
        var extendedBefore = await Year("sub_extended_before", InDays(20));
        extendedBefore.ExtensionStartsAt = extendedBefore.RenewsAt.AddDays(-364);
        await db.SaveChangesAsync();

        Assert.That(await Due(), Is.EqualTo(new[] { "sub_extended_before" }), "the term after an extension is reminded of again");
    }

    [Test]
    public async Task OneReminderPerTermEnd()
    {
        var subscription = await Year("sub_1", InDays(20));

        Assert.That(await Send(), Is.EqualTo(1));
        Assert.That(await Send(), Is.Zero);
        Assert.That(producer.Published, Has.Count.EqualTo(1));
        db.ChangeTracker.Clear();
        Assert.That((await db.Subscriptions.SingleAsync()).RenewalReminderSentFor, Is.EqualTo(subscription.RenewsAt));

        var renewed = await db.Subscriptions.SingleAsync();
        renewed.RenewsAt = renewed.RenewsAt.AddDays(10);
        await db.SaveChangesAsync();
        Assert.That(await Send(), Is.EqualTo(1), "a new term end is reminded of again");
    }

    [Test]
    public async Task ReminderNamesTheTermTheFourWeekPriceAndTheExtension()
    {
        var subscription = await Year("sub_1", InDays(20));
        var request = await SeedCheckout("stripe", "cs_1", premiumYear, 118.63m);
        db.ServicePerformanceDeclarations.Add(StripeExtensionTests.Evidence(request.Id, "Start now", "de"));
        db.PaymentRecords.Add(new PaymentRecord { UserId = (await Buyer()).Id, Provider = "stripe", Currency = "EUR", ExternalOrderId = "in_1", ExternalTransactionId = "sub_1",
            IsSubscriptionPayment = true, BuyerEmail = "buyer@example.test", PaidAt = DateTime.UtcNow.AddDays(-345) });
        await db.SaveChangesAsync();

        await Send();

        var reminder = producer.Published.Single();
        Assert.That((reminder.ConfirmationType, reminder.PaymentProvider, reminder.PaymentMethod, reminder.PaymentProviderTransactionId),
            Is.EqualTo(("subscription_renewal_reminder", "stripe", "stripe", $"renewal-reminder:sub_1:{subscription.RenewsAt:yyyyMMdd}")));
        Assert.That((reminder.PayedAmount, reminder.UserId, reminder.Email, reminder.ProductId, reminder.ProductTitle, reminder.Currency),
            Is.EqualTo((0d, "buyer", "buyer@example.test", premiumYear.Id.ToString(), "52 weeks premium", "EUR")));
        Assert.That(reminder.Timestamp, Is.EqualTo(DateTime.UtcNow).Within(TimeSpan.FromMinutes(1)));
        Assert.That((reminder.SubscriptionBillingWeeks, reminder.SubscriptionRenewalWeeks, reminder.SubscriptionRenewalAmount, reminder.RenewalTaxTreatment),
            Is.EqualTo(((int?)52, (int?)4, (decimal?)11.53m, "included")));
        Assert.That((reminder.SubscriptionTermEndsAt, reminder.SubscriptionExtensionAmount, reminder.SubscriptionExternalId, reminder.LegalLocale),
            Is.EqualTo(((DateTime?)subscription.RenewsAt, (decimal?)118.63m, "sub_1", "de")));
    }

    [Test]
    public async Task ManagedPaymentsReminderStatesNetPrices()
    {
        await Year("sub_1", InDays(20), managed: true);

        await Send();

        var reminder = producer.Published.Single();
        Assert.That((reminder.PaymentMethod, reminder.SubscriptionRenewalAmount, reminder.SubscriptionExtensionAmount, reminder.RenewalTaxTreatment),
            Is.EqualTo(("stripe_managed_payments", (decimal?)9.69m, (decimal?)99.69m, "net")));
    }

    /// <summary>While SUBSCRIPTIONS:EXTENSIONS_ENABLED is off the reminder is still sent, without an extension offer</summary>
    [Test]
    public async Task ReminderHasNoExtensionOfferWhileExtensionsAreOff()
    {
        await Year("sub_1", InDays(20));

        var off = Service(config: new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        Assert.That(await off.SendRenewalReminders(producer, TimeSpan.FromSeconds(5)), Is.EqualTo(1));

        var reminder = producer.Published.Single();
        Assert.That((reminder.SubscriptionExtensionAmount, reminder.SubscriptionRenewalAmount), Is.EqualTo(((decimal?)null, (decimal?)11.53m)));
    }

    [Test]
    public async Task ReminderWithinTheLastDayHasNoExtensionOffer()
    {
        await Year("sub_1", InDays(0.5));

        await Send();

        Assert.That(producer.Published.Single().SubscriptionExtensionAmount, Is.Null);
    }

    /// <summary>At least once: a failed or hanging publish is not recorded as sent and stops the run; the next run retries</summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task UnpublishedRemindersAreRetriedByTheNextRun(bool hangs)
    {
        await Year("sub_1", InDays(20));
        await Year("sub_2", InDays(21));
        producer.Failing = 1;
        producer.Hangs = hangs;

        Assert.That(await Service().SendRenewalReminders(producer, TimeSpan.FromMilliseconds(200)), Is.Zero);
        Assert.That(await db.Subscriptions.CountAsync(s => s.RenewalReminderSentFor != null), Is.Zero);

        Assert.That(await Send(), Is.EqualTo(2));
        Assert.That(producer.Published.Select(p => p.SubscriptionExternalId), Is.EqualTo(new[] { "sub_1", "sub_2" }));
    }

    /// <summary>Regression (review): a reminder that could not be built (here: a corrupt discount code) blocked every later one</summary>
    [Test]
    public async Task AReminderThatCanNotBeBuiltIsSkipped()
    {
        db.DiscountCodes.Add(new DiscountCode { Code = "BROKEN", AmountType = "percent", Amount = 10, PeriodAmounts = new() { [52] = 50 } });
        var broken = await Year("sub_broken", InDays(10));
        broken.DiscountCode = "BROKEN";
        await Year("sub_ok", InDays(20));
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("UPDATE \"DiscountCodes\" SET \"PeriodAmounts\" = 'not json'");
        db.ChangeTracker.Clear();

        Assert.That(await Send(), Is.EqualTo(1));

        Assert.That(producer.Published.Select(p => p.SubscriptionExternalId), Is.EqualTo(new[] { "sub_ok" }));
        Assert.That(await db.Subscriptions.Where(s => s.RenewalReminderSentFor != null).Select(s => s.ExternalId).ToListAsync(),
            Is.EqualTo(new[] { "sub_ok" }), "the broken one is tried again by the next run");
    }

    /// <summary>A backlog is sent over several runs, so it does not flood the email queue ahead of purchase confirmations</summary>
    [Test]
    public async Task RemindersPerRunAreCapped()
    {
        await Year("sub_1", InDays(10));
        await Year("sub_2", InDays(11));
        await Year("sub_3", InDays(12));

        Assert.That(await Service().SendRenewalReminders(producer, TimeSpan.FromSeconds(5), 2), Is.EqualTo(2));
        Assert.That(await Service().SendRenewalReminders(producer, TimeSpan.FromSeconds(5), 2), Is.EqualTo(1));
        Assert.That(producer.Published.Select(p => p.SubscriptionExternalId), Is.EqualTo(new[] { "sub_1", "sub_2", "sub_3" }));
    }

    private sealed class FakeProducer : IPaymentEventProducer
    {
        public List<PaymentEvent> Published { get; } = [];
        public int Failing { get; set; }
        public bool Hangs { get; set; }

        public Task ProduceEvent(PaymentEvent paymentEvent)
        {
            if (Failing-- > 0)
                return Hangs ? new TaskCompletionSource().Task : Task.FromException(new InvalidOperationException("Kafka unreachable"));
            Published.Add(paymentEvent);
            return Task.CompletedTask;
        }
    }
}
