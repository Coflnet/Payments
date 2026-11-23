using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Models.GooglePay;
using Coflnet.Payments.Services.Subscriptions;
using Google;
using Google.Apis.AndroidPublisher.v3.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace Coflnet.Payments.Services;

public partial class GooglePlaySubscriptionTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task EligibilityMapsTheActiveFourWeekSale(bool useConfigSection)
    {
        await AddGooglePlaySale();
        var service = PlayService(saleOffers: useConfigSection
            ? new Dictionary<string, string> { ["GOOGLEPAY:SALE_OFFERS:25"] = "sale-25" }
            : SaleOffers);

        Assert.That(await service.GooglePlayEligibility("buyer", "l_premium"),
            Is.EqualTo(new GooglePlayEligibility(true, null, "premium", "p4w", null, "sale-25", 25)));
        Assert.That(await service.GooglePlayEligibility("buyer", "unavailable"),
            Is.EqualTo(new GooglePlayEligibility(false, "plan_unavailable", null, null, null)));
    }

    [TestCase("""{"25":"sale-25","10":}""", null)]
    [TestCase("""["sale-25"]""", null)]
    [TestCase("null", null)]
    [TestCase("""{"25":"sale-25","10":10}""", null)]
    [TestCase("""{"25":"sale-25","10":null}""", null)]
    [TestCase("""{"25":"sale-25","10":"\uD800"}""", null)]
    [TestCase("""{"25":"sale-25","10.5":"sale-10"}""", null)]
    [TestCase("""{"25":"sale-25","10\u0000":"sale-10"}""", null)]
    [TestCase("""{"25":"sale-25","0":"sale-0"}""", null)]
    [TestCase("""{"25":"sale-25","100":"sale-100"}""", null)]
    [TestCase("""{"25":"sale-25","10":""}""", null)]
    [TestCase("""{"25":"sale-25","10":"Sale-10"}""", null)]
    [TestCase("""{"25":"sale-25","10":"-sale-10"}""", null)]
    [TestCase("""{"25":"sale-25","10":"sale_10"}""", null)]
    [TestCase("""{"25":"sale-25","10":"sale-10\n"}""", null)]
    [TestCase("""{"25":"sale-25","10":"sale-25"}""", null)]
    [TestCase("sale-10", "10:nested")]
    [TestCase("sale-10", "ten")]
    [TestCase("", "10")]
    [TestCase("sale-25", "10")]
    [TestCase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "10")]
    public async Task InvalidSaleOffersDisableTheWholeSetting(string value, string childKey)
    {
        await AddGooglePlaySale();
        var config = childKey == null ? new Dictionary<string, string> { ["GOOGLEPAY:SALE_OFFERS"] = value }
            : new() { ["GOOGLEPAY:SALE_OFFERS:25"] = "sale-25", ["GOOGLEPAY:SALE_OFFERS:" + childKey] = value };
        var logger = new Mock<ILogger<SubscriptionService>>();
        var service = PlayService(saleOffers: config, logger: logger.Object);
        remote.LineItems[0].OfferDetails.OfferId = "sale-25";
        remote.AcknowledgementState = "ACKNOWLEDGEMENT_STATE_PENDING";

        Assert.That(await service.GooglePlayEligibility("buyer", "l_premium"),
            Is.EqualTo(new GooglePlayEligibility(true, null, "premium", "p4w", null)));
        Assert.That((await service.SyncGooglePlay(Token, "buyer")).ErrorMessage, Is.EqualTo("plan_unavailable"));
        Assert.That(await db.Subscriptions.CountAsync(), Is.Zero);
        Assert.That(await PaymentRecords("googleplay"), Is.Zero);
        publisher.Verify(p => p.GetOrderAmounts(It.IsAny<string>()), Times.Never);
        publisher.Verify(p => p.AcknowledgeSubscription(It.IsAny<string>(), It.IsAny<string>()), Times.Never);

        var error = logger.Invocations.Single(i => i.Method.Name == "Log" && i.Arguments[0].Equals(LogLevel.Error));
        Assert.That(error.Arguments[2].ToString(), Is.EqualTo("Invalid GOOGLEPAY:SALE_OFFERS configuration"));
        Assert.That(error.Arguments[3], Is.Null);
    }

    [TestCase("percent", DiscountCode.EveryRenewal, 25)]
    [TestCase("fixed", DiscountCode.FirstPaymentOnly, 25)]
    [TestCase("percent", DiscountCode.FirstPaymentOnly, 25.5)]
    [TestCase("percent", DiscountCode.FirstPaymentOnly, 30)]
    public async Task EligibilityOmitsSalesWithoutAMatchingFirstPaymentOffer(string amountType, string duration, double amount)
    {
        await AddGooglePlaySale(amountType, duration, (decimal)amount);

        Assert.That(await PlayService(saleOffers: SaleOffers).GooglePlayEligibility("buyer", "l_premium"),
            Is.EqualTo(new GooglePlayEligibility(true, null, "premium", "p4w", null)));
    }

    [TestCase("sale-25", 25, -60, true)]
    [TestCase("sale-25", 25, 14, true)]
    [TestCase("sale-25", 25, -61, false)]
    [TestCase("sale-25", 25, 15, false)]
    [TestCase("unknown", 25, -10, false)]
    [TestCase("sale-25", 30, -10, false)]
    public async Task FirstGrantRequiresAMatchingSaleAtPurchaseStart(string offerId, int percent, int minutesAfterExpiry, bool accepted)
    {
        var sale = await AddGooglePlaySale(percent: percent);
        sale.ExpiresAt = DateTime.UtcNow.AddHours(-1);
        sale.StartsAt = sale.ExpiresAt.Value.AddHours(-1);
        await db.SaveChangesAsync();
        remote.StartTimeDateTimeOffset = sale.ExpiresAt.Value.AddMinutes(minutesAfterExpiry);
        remote.LineItems[0].OfferDetails.OfferId = offerId;
        remote.AcknowledgementState = "ACKNOWLEDGEMENT_STATE_PENDING";
        publisher.Setup(p => p.GetOrderAmounts(Order)).ReturnsAsync((new Money { CurrencyCode = "EUR", Units = 7 }, null));

        var result = await PlayService(saleOffers: SaleOffers).SyncGooglePlay(Token, "buyer");

        Assert.That(result.IsValid, Is.EqualTo(accepted));
        Assert.That(result.ErrorMessage, Is.EqualTo(accepted ? null : "plan_unavailable"));
        Assert.That(await Grants(SubscriptionProviders.GrantReference("googleplay", Order)), Is.EqualTo(accepted ? 1 : 0));
        Assert.That(await PaymentRecords("googleplay"), Is.EqualTo(accepted ? 1 : 0));
        publisher.Verify(p => p.GetOrderAmounts(It.IsAny<string>()), Times.Exactly(accepted ? 1 : 0));
        publisher.Verify(p => p.AcknowledgeSubscription(It.IsAny<string>(), It.IsAny<string>()), Times.Exactly(accepted ? 1 : 0));
    }

    [TestCase("manual")]
    [TestCase("inactive")]
    [TestCase("all_products")]
    [TestCase("fixed")]
    [TestCase("forever")]
    [TestCase("exhausted")]
    [TestCase("undated")]
    [TestCase("capped")]
    public async Task IneligibleSalesGiveNoOfferAndCannotAuthorizeAFirstGrant(string reason)
    {
        remote.StartTimeDateTimeOffset = DateTimeOffset.UtcNow.AddDays(-1);
        var sale = await AddGooglePlaySale();
        switch (reason)
        {
            case "manual": sale.AutoApply = false; break;
            case "inactive": sale.IsActive = false; break;
            case "all_products": sale.SubscriptionOnly = false; break;
            case "fixed": sale.AmountType = "fixed"; break;
            case "forever": sale.Duration = DiscountCode.EveryRenewal; break;
            case "exhausted": sale.MaxRedemptions = sale.TimesRedeemed = 1; break;
            case "undated": sale.StartsAt = null; break;
            case "capped": sale.MaxRedemptions = 1; break;
        }
        await db.SaveChangesAsync();
        remote.LineItems[0].OfferDetails.OfferId = "sale-25";
        remote.AcknowledgementState = "ACKNOWLEDGEMENT_STATE_PENDING";
        var service = PlayService(saleOffers: SaleOffers);

        Assert.That(await service.GooglePlayEligibility("buyer", "l_premium"),
            Is.EqualTo(new GooglePlayEligibility(true, null, "premium", "p4w", null)));
        var result = await service.SyncGooglePlay(Token, "buyer");

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("plan_unavailable"));
        Assert.That(await db.Subscriptions.CountAsync(), Is.Zero);
        publisher.Verify(p => p.GetOrderAmounts(It.IsAny<string>()), Times.Never);
        publisher.Verify(p => p.AcknowledgeSubscription(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [TestCase("sale-25", 60, false)]
    [TestCase("sale-10", 60, true)]
    [TestCase("sale-25", 5, true)]
    public async Task OnlyTheWinnersAtPurchaseStartAndFifteenMinutesEarlierAuthorizeOffers(string offerId, int newSaleAge, bool accepted)
    {
        var start = remote.StartTimeDateTimeOffset.Value.UtcDateTime;
        var older = await AddGooglePlaySale();
        older.StartsAt = older.CreatedAt = start.AddHours(-2);
        var newer = await AddGooglePlaySale(percent: 10, code: "NEW-SALE");
        newer.StartsAt = newer.CreatedAt = start.AddMinutes(-newSaleAge);
        await db.SaveChangesAsync();
        remote.LineItems[0].OfferDetails.OfferId = offerId;
        var config = new Dictionary<string, string> { ["GOOGLEPAY:SALE_OFFERS"] = """{"25":"sale-25","10":"sale-10"}""" };

        var result = await PlayService(saleOffers: config).SyncGooglePlay(Token, "buyer");

        Assert.That(result.IsValid, Is.EqualTo(accepted));
        Assert.That(result.ErrorMessage, Is.EqualTo(accepted ? null : "plan_unavailable"));
        Assert.That(await PaymentRecords("googleplay"), Is.EqualTo(accepted ? 1 : 0));
        if (accepted)
        {
            var code = await db.Subscriptions.Select(s => s.DiscountCode).SingleAsync();
            Assert.That(code, Is.EqualTo(offerId == "sale-10" ? newer.Code : older.Code));
        }
    }

    [Test]
    public async Task FirstOfferOrderRecordsThePaidTotalAndRedeemsOnce()
    {
        await AddGooglePlaySale();
        remote.LineItems[0].OfferDetails.OfferId = "sale-25";
        publisher.Setup(p => p.GetOrderAmounts(Order)).ReturnsAsync((new Money { CurrencyCode = "USD", Units = 6, Nanos = 550000000 }, null));
        var service = PlayService(saleOffers: SaleOffers);

        Assert.That((await service.SyncGooglePlay(Token, "buyer")).IsValid, Is.True);
        Assert.That((await service.SyncGooglePlayNotification(Token)).IsValid, Is.True);
        Assert.That((await service.SyncGooglePlay(Token, "buyer")).IsValid, Is.True);

        var subscription = await db.Subscriptions.SingleAsync();
        var payment = await db.PaymentRecords.SingleAsync();
        Assert.That(subscription.PriceCents, Is.EqualTo(969));
        Assert.That(subscription.PaymentAmount, Is.EqualTo("9.69 EUR"));
        Assert.That(payment.GrossAmount, Is.EqualTo(6.55m));
        Assert.That(payment.TaxAmount, Is.Zero);
        Assert.That(payment.Subtotal, Is.EqualTo(payment.GrossAmount));
        Assert.That(payment.NetAmount, Is.EqualTo(payment.GrossAmount));
        Assert.That(payment.Currency, Is.EqualTo("USD"));
        Assert.That(payment.TaxRemittedByProcessor, Is.True);
        Assert.That(await db.DiscountCodes.Select(c => c.TimesRedeemed).SingleAsync(), Is.EqualTo(1));
        Assert.That(await service.GooglePlayEligibility("buyer", "l_premium"),
            Is.EqualTo(new GooglePlayEligibility(false, "active_subscription", "premium", "p4w", "googleplay")));
        publisher.Verify(p => p.GetOrderAmounts(It.IsAny<string>()), Times.Once);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task OfferRenewalStillGrantsAfterTheSaleAndConfigAreGone(bool lookupSucceeds)
    {
        var sale = await AddGooglePlaySale();
        remote.LineItems[0].OfferDetails.OfferId = "sale-25";
        publisher.Setup(p => p.GetOrderAmounts(Order)).ReturnsAsync((new Money { CurrencyCode = "EUR", Units = 7 }, null));
        Assert.That((await PlayService(saleOffers: SaleOffers).SyncGooglePlay(Token, "buyer")).IsValid, Is.True);
        sale.IsActive = false;
        await db.SaveChangesAsync();
        Renew();
        var renewal = remote.LineItems[0].LatestSuccessfulOrderId;
        publisher.Setup(p => p.GetOrderAmounts(renewal)).ReturnsAsync((lookupSucceeds ? new Money { CurrencyCode = "EUR", Units = 10 } : null, null));
        var service = PlayService();

        Assert.That((await service.SyncGooglePlayNotification(Token)).IsValid, Is.True);
        Assert.That((await service.SyncGooglePlayNotification(Token)).IsValid, Is.True);

        var payment = await db.PaymentRecords.SingleAsync(p => p.ExternalOrderId == renewal);
        Assert.That(payment.GrossAmount, Is.EqualTo(lookupSucceeds ? 10m : 9.69m));
        Assert.That(await db.DiscountCodes.Select(c => c.TimesRedeemed).SingleAsync(), Is.EqualTo(1));
        publisher.Verify(p => p.GetOrderAmounts(renewal), Times.Once);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task ValidatedPendingOfferGrantsAfterTheSaleEndsAndCanExpire(bool hasStartTime)
    {
        var sale = await AddGooglePlaySale();
        SetPendingOffer();
        if (!hasStartTime)
            remote.StartTimeDateTimeOffset = null;

        var pending = await PlayService(saleOffers: SaleOffers).SyncGooglePlay(Token, "buyer");

        Assert.That(pending, Is.EqualTo(new GooglePlaySubscriptionResult(false, "pending", null)));
        Assert.That(await db.Subscriptions.AsNoTracking().Select(s => s.DiscountCode).SingleAsync(), Is.EqualTo(sale.Code));
        Assert.That(await PaymentRecords("googleplay"), Is.Zero);
        Assert.That(await db.DiscountCodes.Select(c => c.TimesRedeemed).SingleAsync(), Is.Zero);

        sale.IsActive = false;
        await db.SaveChangesAsync();
        remote.SubscriptionState = "SUBSCRIPTION_STATE_ACTIVE";
        remote.StartTimeDateTimeOffset = DateTimeOffset.UtcNow;
        remote.LineItems[0].LatestSuccessfulOrderId = Order;
        remote.LineItems[0].ExpiryTimeDateTimeOffset = expiry;
        publisher.Setup(p => p.GetOrderAmounts(Order)).ThrowsAsync(new TimeoutException());
        var service = PlayService();

        Assert.That((await service.SyncGooglePlayNotification(Token)).IsValid, Is.True);
        Assert.That((await service.SyncGooglePlayNotification(Token)).IsValid, Is.True);
        Assert.That(await db.PaymentRecords.Select(p => p.GrossAmount).SingleAsync(), Is.EqualTo(7.27m));
        Assert.That(await db.DiscountCodes.Select(c => c.TimesRedeemed).SingleAsync(), Is.EqualTo(1));

        remote.SubscriptionState = "SUBSCRIPTION_STATE_EXPIRED";
        remote.StartTimeDateTimeOffset = null;
        var ended = DateTime.UtcNow.AddMinutes(-1);
        remote.LineItems[0].ExpiryTimeDateTimeOffset = ended;

        Assert.That(await service.SyncGooglePlayNotification(Token), Is.EqualTo(new GooglePlaySubscriptionResult(false, "expired", null)));
        Assert.That(await db.Subscriptions.Select(s => s.Status).SingleAsync(), Is.EqualTo("expired"));
        Assert.That(await db.OwnerShips.Select(o => o.Expires).SingleAsync(), Is.LessThanOrEqualTo(ended));
        publisher.Verify(p => p.GetOrderAmounts(It.IsAny<string>()), Times.Once);
    }

    [Test]
    public async Task PendingOfferWithoutAStartTimeOrSaleIsRejected()
    {
        SetPendingOffer();
        remote.StartTimeDateTimeOffset = null;

        var result = await PlayService(saleOffers: SaleOffers).SyncGooglePlay(Token, "buyer");

        Assert.That(result.ErrorMessage, Is.EqualTo("plan_unavailable"));
        Assert.That(await db.Subscriptions.CountAsync(), Is.Zero);
        publisher.Verify(p => p.GetOrderAmounts(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task ActiveOfferWithoutAStartTimeIsRejectedDuringARunningSale()
    {
        await AddGooglePlaySale();
        remote.StartTimeDateTimeOffset = null;
        remote.LineItems[0].OfferDetails.OfferId = "sale-25";
        remote.AcknowledgementState = "ACKNOWLEDGEMENT_STATE_PENDING";

        var result = await PlayService(saleOffers: SaleOffers).SyncGooglePlay(Token, "buyer");

        Assert.That(result.ErrorMessage, Is.EqualTo("plan_unavailable"));
        Assert.That(await db.Subscriptions.CountAsync(), Is.Zero);
        publisher.Verify(p => p.GetOrderAmounts(It.IsAny<string>()), Times.Never);
        publisher.Verify(p => p.AcknowledgeSubscription(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task PreviouslyGrantedRowWithNoStoredCodeCannotStartUsingAnOffer()
    {
        var service = PlayService();
        Assert.That((await service.SyncGooglePlay(Token, "buyer")).IsValid, Is.True);
        Assert.That(await db.Subscriptions.Select(s => s.DiscountCode).SingleAsync(), Is.Null);
        Renew();
        remote.LineItems[0].OfferDetails.OfferId = "sale-25";
        remote.AcknowledgementState = "ACKNOWLEDGEMENT_STATE_PENDING";

        var result = await service.SyncGooglePlayNotification(Token);

        Assert.That(result, Is.EqualTo(new GooglePlaySubscriptionResult(false, null, "plan_unavailable")));
        Assert.That(await PaymentRecords("googleplay"), Is.EqualTo(1));
        publisher.Verify(p => p.GetOrderAmounts(It.IsAny<string>()), Times.Never);
        publisher.Verify(p => p.AcknowledgeSubscription(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [TestCase("USD", 710, 55, 655)] // Tax added to the US price at sale.
    [TestCase("EUR", 655, 105, 550)] // Tax included in the displayed EU price.
    [TestCase("EUR", 105, 105, 0)]
    public async Task OfferPaymentRecordsTotalAndTaxInTheOrderCurrency(string currency, int totalCents, int taxCents, int subtotalCents)
    {
        await AddGooglePlaySale();
        remote.LineItems[0].OfferDetails.OfferId = "sale-25";
        var total = new Money { CurrencyCode = currency, Units = totalCents / 100, Nanos = totalCents % 100 * 10_000_000 };
        var tax = new Money { CurrencyCode = currency, Units = taxCents / 100, Nanos = taxCents % 100 * 10_000_000 };
        publisher.Setup(p => p.GetOrderAmounts(Order)).ReturnsAsync((total, tax));

        Assert.That((await PlayService(saleOffers: SaleOffers).SyncGooglePlay(Token, "buyer")).IsValid, Is.True);

        var payment = await db.PaymentRecords.SingleAsync();
        Assert.That(payment.Currency, Is.EqualTo(currency));
        Assert.That(payment.GrossAmount, Is.EqualTo(totalCents / 100m));
        Assert.That(payment.TaxAmount, Is.EqualTo(taxCents / 100m));
        Assert.That(payment.Subtotal, Is.EqualTo(subtotalCents / 100m));
        Assert.That(payment.NetAmount, Is.EqualTo(subtotalCents / 100m));
        Assert.That(payment.TaxRemittedByProcessor, Is.True);
    }

    [TestCase("USD", 55)]
    [TestCase("EUR", -55)]
    [TestCase("EUR", 711)]
    public async Task ImplausibleOrderTaxIsLoggedAndRecordedAsZero(string taxCurrency, int taxCents)
    {
        await AddGooglePlaySale();
        remote.LineItems[0].OfferDetails.OfferId = "sale-25";
        var total = new Money { CurrencyCode = "EUR", Units = 7, Nanos = 100_000_000 };
        var tax = new Money { CurrencyCode = taxCurrency, Units = taxCents / 100, Nanos = taxCents % 100 * 10_000_000 };
        publisher.Setup(p => p.GetOrderAmounts(Order)).ReturnsAsync((total, tax));
        var logger = new Mock<ILogger<SubscriptionService>>();

        Assert.That((await PlayService(saleOffers: SaleOffers, logger: logger.Object).SyncGooglePlay(Token, "buyer")).IsValid, Is.True);

        var payment = await db.PaymentRecords.SingleAsync();
        Assert.That(payment.GrossAmount, Is.EqualTo(7.10m));
        Assert.That(payment.TaxAmount, Is.Zero);
        Assert.That(payment.Subtotal, Is.EqualTo(payment.GrossAmount));
        Assert.That(payment.NetAmount, Is.EqualTo(payment.GrossAmount));
        var error = logger.Invocations.Single(i => i.Method.Name == "Log" && i.Arguments[0].Equals(LogLevel.Error));
        Assert.That(error.Arguments[2].ToString(), Is.EqualTo("Google Play offer order lookup returned invalid tax"));
        Assert.That(error.Arguments[3], Is.Null);
    }

    [Test]
    public async Task OrderWithoutTaxRecordsZeroWithoutLoggingAnError()
    {
        await AddGooglePlaySale();
        remote.LineItems[0].OfferDetails.OfferId = "sale-25";
        var total = new Money { CurrencyCode = "EUR", Units = 7, Nanos = 100_000_000 };
        publisher.Setup(p => p.GetOrderAmounts(Order)).ReturnsAsync((total, (Money)null));
        var logger = new Mock<ILogger<SubscriptionService>>();

        Assert.That((await PlayService(saleOffers: SaleOffers, logger: logger.Object).SyncGooglePlay(Token, "buyer")).IsValid, Is.True);

        var payment = await db.PaymentRecords.SingleAsync();
        Assert.That(payment.GrossAmount, Is.EqualTo(7.10m));
        Assert.That(payment.TaxAmount, Is.Zero);
        Assert.That(payment.NetAmount, Is.EqualTo(7.10m));
        Assert.That(logger.Invocations.Any(i => i.Method.Name == "Log" && i.Arguments[0].Equals(LogLevel.Error)), Is.False);
    }

    [TestCase("EUR", 9, 690000000, 7.27)]
    [TestCase("ISK", 101, 0, 76)]
    [TestCase("KWD", 9, 690000000, 7.268)]
    public async Task FailedOfferLookupStillGrantsUsingTheDiscountRoundedToTheMinorUnit(string currency, int units, int nanos, double fallback)
    {
        await AddGooglePlaySale();
        var item = remote.LineItems[0];
        item.OfferDetails.OfferId = "sale-25";
        item.AutoRenewingPlan.RecurringPrice = new Money { CurrencyCode = currency, Units = units, Nanos = nanos };
        remote.AcknowledgementState = "ACKNOWLEDGEMENT_STATE_PENDING";
        publisher.Setup(p => p.GetOrderAmounts(Order)).ThrowsAsync(
            new GoogleApiException("androidpublisher", $"{Order} {Token}") { HttpStatusCode = HttpStatusCode.Forbidden });
        var logger = new Mock<ILogger<SubscriptionService>>();

        Assert.That((await PlayService(saleOffers: SaleOffers, logger: logger.Object).SyncGooglePlay(Token, "buyer")).IsValid, Is.True);

        var payment = await db.PaymentRecords.SingleAsync();
        Assert.That(payment.GrossAmount, Is.EqualTo((decimal)fallback));
        Assert.That(payment.Subtotal, Is.EqualTo(payment.GrossAmount));
        Assert.That(payment.NetAmount, Is.EqualTo(payment.GrossAmount));
        Assert.That(payment.TaxAmount, Is.Zero);
        Assert.That(payment.Currency, Is.EqualTo(currency));
        publisher.Verify(p => p.AcknowledgeSubscription("premium", Token), Times.Once);
        var error = logger.Invocations.Single(i => i.Method.Name == "Log" && i.Arguments[0].Equals(LogLevel.Error));
        Assert.That(error.Arguments[2].ToString(), Is.EqualTo("Google Play offer order lookup failed (GoogleApiException, HTTP 403)"));
        Assert.That(error.Arguments[3], Is.Null);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task LicenseTestOfferChecksTheOrderWithoutRecordingRevenueOrRedemption(bool lookupSucceeds)
    {
        await AddGooglePlaySale();
        remote.LineItems[0].OfferDetails.OfferId = "sale-25";
        remote.TestPurchase = new TestPurchase();
        var lookup = publisher.Setup(p => p.GetOrderAmounts(Order));
        if (lookupSucceeds)
            lookup.ReturnsAsync((new Money { CurrencyCode = "EUR" }, null));
        else
            lookup.ThrowsAsync(new TimeoutException(Order));
        var logger = new Mock<ILogger<SubscriptionService>>();

        Assert.That((await PlayService(saleOffers: SaleOffers, logger: logger.Object).SyncGooglePlay(Token, "buyer")).IsValid, Is.True);

        Assert.That(await PaymentRecords("googleplay"), Is.Zero);
        Assert.That(await db.DiscountCodes.Select(c => c.TimesRedeemed).SingleAsync(), Is.Zero);
        publisher.Verify(p => p.GetOrderAmounts(Order), Times.Once);
        var log = logger.Invocations.Single(i => i.Method.Name == "Log" && i.Arguments[0].Equals(LogLevel.Information)
            && i.Arguments[2].ToString().Contains("order lookup"));
        Assert.That(log.Arguments[0], Is.EqualTo(LogLevel.Information));
        Assert.That(log.Arguments[2].ToString(), Is.EqualTo($"Google Play test offer order lookup succeeded {lookupSucceeds}"));
        Assert.That(log.Arguments[3], Is.Null);
    }

    [Test]
    public async Task ARedemptionWriteFailureDoesNotPreventGrantOrAcknowledgement()
    {
        await AddGooglePlaySale();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER fail_redemption BEFORE UPDATE OF TimesRedeemed ON DiscountCodes
            BEGIN SELECT RAISE(ABORT, 'Failed to count sale redemption'); END;
            """);
        remote.LineItems[0].OfferDetails.OfferId = "sale-25";
        remote.AcknowledgementState = "ACKNOWLEDGEMENT_STATE_PENDING";
        publisher.Setup(p => p.GetOrderAmounts(Order)).ReturnsAsync((new Money { CurrencyCode = "EUR", Units = 7 }, null));

        Assert.That((await PlayService(saleOffers: SaleOffers).SyncGooglePlay(Token, "buyer")).IsValid, Is.True);

        Assert.That(await PaymentRecords("googleplay"), Is.EqualTo(1));
        Assert.That(await db.DiscountCodes.Select(c => c.TimesRedeemed).SingleAsync(), Is.Zero);
        publisher.Verify(p => p.AcknowledgeSubscription("premium", Token), Times.Once);
    }

    private void SetPendingOffer()
    {
        remote.SubscriptionState = "SUBSCRIPTION_STATE_PENDING";
        remote.LineItems[0].OfferDetails.OfferId = "sale-25";
        remote.LineItems[0].LatestSuccessfulOrderId = null;
        remote.LineItems[0].ExpiryTimeDateTimeOffset = null;
    }

    private async Task<DiscountCode> AddGooglePlaySale(string amountType = "percent", string duration = DiscountCode.FirstPaymentOnly,
        decimal percent = 25, string code = "SALE")
    {
        var sale = new DiscountCode
        {
            Code = code, AutoApply = true, SubscriptionOnly = true, AmountType = amountType, Duration = duration,
            Amount = 40, PeriodAmounts = new() { [4] = percent },
            StartsAt = remote.StartTimeDateTimeOffset.Value.UtcDateTime.AddMinutes(-1)
        };
        db.DiscountCodes.Add(sale);
        await db.SaveChangesAsync();
        return sale;
    }
}
