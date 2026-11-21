using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Models.GooglePay;
using Coflnet.Payments.Services.Subscriptions;
using Google;
using Google.Apis.AndroidPublisher.v3.Data;
using Google.Apis.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;
using NUnit.Framework;
using Payments.Controllers;

namespace Coflnet.Payments.Services;

public class GooglePlaySubscriptionTests : DirectSubscriptionTestBase
{
    private const string Token = "private-purchase-token";
    private const string Order = "GPA.1234-5678-9012-34567..0";
    private const string Audience = "https://payments.example/callback/googlepay";
    private const string PushAccount = "push@example.iam.gserviceaccount.com";
    private Mock<IGooglePlayPublisher> publisher;
    private SubscriptionPurchaseV2 remote;
    private DateTime expiry;

    [SetUp]
    public void SetUpGooglePlay()
    {
        expiry = DateTime.UtcNow.AddDays(28);
        remote = new SubscriptionPurchaseV2
        {
            SubscriptionState = "SUBSCRIPTION_STATE_ACTIVE",
            AcknowledgementState = "ACKNOWLEDGEMENT_STATE_ACKNOWLEDGED",
            RegionCode = "DE",
            StartTimeDateTimeOffset = DateTimeOffset.UtcNow,
            ExternalAccountIdentifiers = new ExternalAccountIdentifiers { ObfuscatedExternalAccountId = "buyer" },
            LineItems = new List<SubscriptionPurchaseLineItem>
            {
                new()
                {
                    ProductId = "premium", LatestSuccessfulOrderId = Order,
                    ExpiryTimeDateTimeOffset = new DateTimeOffset(expiry),
                    OfferDetails = new OfferDetails { BasePlanId = "p4w" },
                    AutoRenewingPlan = new AutoRenewingPlan
                    {
                        AutoRenewEnabled = true,
                        RecurringPrice = new Money { CurrencyCode = "EUR", Units = 9, Nanos = 690000000 }
                    }
                }
            }
        };
        publisher = new Mock<IGooglePlayPublisher>();
        publisher.Setup(p => p.GetSubscriptionV2(It.IsAny<string>())).ReturnsAsync(() => remote);
        publisher.Setup(p => p.AcknowledgeSubscription(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        publisher.Setup(p => p.IsVoidedPurchase(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);
    }

    private SubscriptionService PlayService(bool acceptTestPurchases = true, string allowedRegions = "DE") => new(NullLogger<SubscriptionService>.Instance, transactions, users, products, db,
        new LemonSqueezyService(new ConfigurationBuilder().Build(), NullLogger<LemonSqueezyService>.Instance, db,
            new VariantCacheService(NullLogger<VariantCacheService>.Instance)), tax: Tax(),
        config: new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["GOOGLEPAY:SUBSCRIPTION_PLANS:premium"] = "l_premium",
            ["GOOGLEPAY:SUBSCRIPTION_PLANS:premium_plus"] = "l_prem_plus",
            ["GOOGLEPAY:ALLOWED_REGIONS"] = allowedRegions,
            ["GOOGLEPAY:ACCEPT_TEST_PURCHASES"] = acceptTestPurchases.ToString()
        }).Build(), googlePlay: publisher.Object);

    private CallbackController PlayCallback(SubscriptionService service, string audience = Audience, string account = PushAccount)
    {
        var callback = Callback(service, new Dictionary<string, string>
        {
            ["GOOGLEPAY:PUBSUB_AUDIENCE"] = audience,
            ["GOOGLEPAY:PUBSUB_SERVICE_ACCOUNT"] = account
        }, googlePlay: publisher.Object);
        callback.Request.Headers.Authorization = "Bearer test-jwt";
        callback.ValidateGooglePlayPushToken = _ => Task.FromResult(PlayPushClaims());
        return callback;
    }

    private static GoogleJsonWebSignature.Payload PlayPushClaims(string audience = Audience, string account = PushAccount,
        bool verified = true, string issuer = "https://accounts.google.com") => new()
    {
        Issuer = issuer, Audience = audience, Email = account, EmailVerified = verified
    };

    private static Task<ActionResult<GooglePlaySubscriptionResult>> CompletePurchase(SubscriptionService service) =>
        new GooglePlaySubscriptionController(service).Complete(new GooglePlaySubscriptionRequest
        {
            UserId = "buyer", PackageName = "com.coflnet.sky", SubscriptionId = "premium", PurchaseToken = Token
        });

    [Test]
    public async Task CompleteVerifiesAndPersistsThePurchaseAndPaidPeriod()
    {
        var result = (await CompletePurchase(PlayService())).Value;

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Status, Is.EqualTo("active"));
        var subscription = await db.Subscriptions.Include(s => s.Product).Include(s => s.User).SingleAsync();
        Assert.That(subscription.ProviderSlug, Is.EqualTo("googleplay"));
        Assert.That(subscription.ProviderToken, Is.EqualTo(Token));
        Assert.That(subscription.ExternalId, Is.EqualTo("gp_" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(Token)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_')));
        Assert.That(subscription.User.ExternalId, Is.EqualTo("buyer"));
        Assert.That(subscription.Product.Slug, Is.EqualTo("l_premium"));
        Assert.That(subscription.RenewsAt, Is.EqualTo(expiry).Within(TimeSpan.FromSeconds(1)));
        Assert.That((await Access(subscription.ExternalId)).Expires, Is.InRange(expiry, expiry.AddHours(3)));
        var payment = await db.PaymentRecords.SingleAsync();
        Assert.That(payment.ExternalOrderId, Is.EqualTo(Order));
        Assert.That(payment.Provider, Is.EqualTo("googleplay"));
        Assert.That(payment.IsSubscriptionPayment, Is.True);
        Assert.That(payment.TaxRemittedByProcessor, Is.True);
        Assert.That(payment.GrossAmount, Is.EqualTo(9.69m));
        Assert.That(payment.Country, Is.EqualTo("DE"));
        Assert.That((await Buyer()).Balance, Is.Zero);
    }

    [Test]
    public async Task CompleteAndNotificationRedeliveryGrantAndRecordOnlyOnce()
    {
        var service = PlayService();
        await service.SyncGooglePlay(Token, "buyer");
        var subscription = await db.Subscriptions.SingleAsync();
        var accessExpiry = (await Access(subscription.ExternalId)).Expires;
        var transactionsBefore = await db.FiniteTransactions.CountAsync();

        Assert.That(await PostGooglePlay(PlayCallback(service), SubscriptionNotification(), wrapped: true), Is.TypeOf<OkResult>());
        Assert.That((await service.SyncGooglePlay(Token, "buyer")).IsValid, Is.True);

        Assert.That(await db.Subscriptions.CountAsync(), Is.EqualTo(1));
        Assert.That(await PaymentRecords("googleplay"), Is.EqualTo(1));
        Assert.That(await db.FiniteTransactions.CountAsync(), Is.EqualTo(transactionsBefore));
        Assert.That((await Access(subscription.ExternalId)).Expires, Is.EqualTo(accessExpiry));
    }

    [Test]
    public async Task BusyCompletionReturnsRetryableProcessingResult()
    {
        var service = PlayService();
        await service.SyncGooglePlay(Token, "buyer");
        var subscription = await db.Subscriptions.SingleAsync();
        subscription.ChangeLockUntil = DateTime.UtcNow.AddMinutes(5);
        await db.SaveChangesAsync();

        var result = (await CompletePurchase(service)).Result;

        Assert.That(result, Is.TypeOf<ConflictObjectResult>());
        Assert.That(((ConflictObjectResult)result).Value, Is.EqualTo(new GooglePlaySubscriptionResult(false, "processing", null)));
        Assert.That(await PaymentRecords("googleplay"), Is.EqualTo(1));
    }

    [Test]
    public async Task PendingPurchaseDoesNotGrantOrAcknowledge()
    {
        remote.SubscriptionState = "SUBSCRIPTION_STATE_PENDING";
        remote.AcknowledgementState = "ACKNOWLEDGEMENT_STATE_PENDING";

        var result = await PlayService().SyncGooglePlay(Token, "buyer");

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Status, Is.EqualTo("pending"));
        Assert.That(await db.OwnerShips.CountAsync(), Is.Zero);
        Assert.That(await db.FiniteTransactions.CountAsync(), Is.Zero);
        Assert.That(await PaymentRecords("googleplay"), Is.Zero);
        publisher.Verify(p => p.AcknowledgeSubscription(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [TestCase("ACKNOWLEDGEMENT_STATE_PENDING", 1)]
    [TestCase("ACKNOWLEDGEMENT_STATE_ACKNOWLEDGED", 0)]
    public async Task OnlyUnacknowledgedPaidPurchaseIsAcknowledged(string state, int calls)
    {
        remote.AcknowledgementState = state;
        Assert.That((await PlayService().SyncGooglePlay(Token, "buyer")).IsValid, Is.True);
        publisher.Verify(p => p.AcknowledgeSubscription("premium", Token), Times.Exactly(calls));
    }

    [Test]
    public async Task ClaimedUserMismatchCannotCreateOrGrantSubscription()
    {
        var result = await PlayService().SyncGooglePlay(Token, "another-buyer");
        Assert.That(result.IsValid, Is.False);
        Assert.That(await db.Subscriptions.CountAsync(), Is.Zero);
        Assert.That(await db.FiniteTransactions.CountAsync(), Is.Zero);
        Assert.That(await PaymentRecords("googleplay"), Is.Zero);
    }

    [Test]
    public async Task AlreadyBoundTokenCannotBeClaimedByAnotherUser()
    {
        var service = PlayService();
        await service.SyncGooglePlay(Token, "buyer");
        remote.ExternalAccountIdentifiers.ObfuscatedExternalAccountId = "another-buyer";

        Assert.That((await service.SyncGooglePlay(Token, "another-buyer")).IsValid, Is.False);
        Assert.That((await db.Subscriptions.Include(s => s.User).SingleAsync()).User.ExternalId, Is.EqualTo("buyer"));
        Assert.That(await PaymentRecords("googleplay"), Is.EqualTo(1));
    }

    [Test]
    public async Task UnlinkedPurchaseWithoutAccountIdentityCannotGrant()
    {
        remote.ExternalAccountIdentifiers = null;
        remote.AcknowledgementState = "ACKNOWLEDGEMENT_STATE_PENDING";
        var result = await PlayService().SyncGooglePlayNotification(Token);
        Assert.That(result.ErrorMessage, Is.EqualTo("missing_user"));
        Assert.That(await db.Subscriptions.CountAsync(), Is.Zero);
        Assert.That(await db.OwnerShips.CountAsync(), Is.Zero);
        Assert.That(await PaymentRecords("googleplay"), Is.Zero);
        publisher.Verify(p => p.AcknowledgeSubscription(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task CompletionBindsAnUnlinkedPurchaseWithoutAccountIdentity()
    {
        remote.ExternalAccountIdentifiers = null;
        remote.LinkedPurchaseToken = "unknown-older-token";
        remote.AcknowledgementState = "ACKNOWLEDGEMENT_STATE_PENDING";
        var service = PlayService();

        Assert.That((await CompletePurchase(service)).Value.IsValid, Is.True);
        Assert.That((await db.Subscriptions.Include(s => s.User).SingleAsync()).User.ExternalId, Is.EqualTo("buyer"));
        publisher.Verify(p => p.AcknowledgeSubscription("premium", Token), Times.Once);
    }

    [Test]
    public async Task PurchaseWithOfferCannotGrantOrAcknowledge()
    {
        remote.LineItems[0].OfferDetails.OfferId = "intro";
        remote.AcknowledgementState = "ACKNOWLEDGEMENT_STATE_PENDING";

        var result = await PlayService().SyncGooglePlay(Token, "buyer");

        Assert.That(result.ErrorMessage, Is.EqualTo("plan_unavailable"));
        Assert.That(await db.Subscriptions.CountAsync(), Is.Zero);
        Assert.That(await db.OwnerShips.CountAsync(), Is.Zero);
        publisher.Verify(p => p.AcknowledgeSubscription(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [TestCase("unknown", "p4w")]
    [TestCase("premium", "p1m")]
    public async Task UnknownProductOrBasePlanCannotGrant(string product, string basePlan)
    {
        remote.LineItems[0].ProductId = product;
        remote.LineItems[0].OfferDetails.BasePlanId = basePlan;
        Assert.That((await PlayService().SyncGooglePlay(Token, "buyer")).IsValid, Is.False);
        Assert.That(await db.FiniteTransactions.CountAsync(), Is.Zero);
        Assert.That(await PaymentRecords("googleplay"), Is.Zero);
    }

    private void Renew()
    {
        expiry = expiry.AddDays(28);
        remote.LineItems[0].LatestSuccessfulOrderId = Order[..^1] + "1";
        remote.LineItems[0].ExpiryTimeDateTimeOffset = new DateTimeOffset(expiry);
    }

    [Test]
    public async Task RenewalUsesItsOwnOrderAndProviderExpiryExactlyOnce()
    {
        var service = PlayService();
        await service.SyncGooglePlay(Token, "buyer");
        Renew();
        await service.SyncGooglePlayNotification(Token);
        await service.SyncGooglePlayNotification(Token);

        Assert.That(await PaymentRecords("googleplay"), Is.EqualTo(2));
        var subscription = await db.Subscriptions.SingleAsync();
        Assert.That((await Access(subscription.ExternalId)).Expires, Is.InRange(expiry, expiry.AddHours(3)));
        Assert.That(subscription.RenewsAt, Is.EqualTo(expiry).Within(TimeSpan.FromSeconds(1)));
    }

    [TestCase("SUBSCRIPTION_STATE_IN_GRACE_PERIOD", "past_due", true)]
    [TestCase("SUBSCRIPTION_STATE_ON_HOLD", "unpaid", false)]
    [TestCase("SUBSCRIPTION_STATE_CANCELED", "cancelled", true)]
    public async Task ProviderStateControlsAccessWithoutGrantingAnotherPeriod(string state, string status, bool accessible)
    {
        var service = PlayService();
        await service.SyncGooglePlay(Token, "buyer");
        var before = await db.FiniteTransactions.CountAsync();
        remote.SubscriptionState = state;
        await service.SyncGooglePlayNotification(Token);

        var subscription = await db.Subscriptions.SingleAsync();
        Assert.That(subscription.Status, Is.EqualTo(status));
        var access = await Access(subscription.ExternalId);
        Assert.That(access != null && access.Expires > DateTime.UtcNow, Is.EqualTo(accessible));
        Assert.That(await db.FiniteTransactions.CountAsync(), Is.EqualTo(before));
        Assert.That(await PaymentRecords("googleplay"), Is.EqualTo(1));
        if (state == "SUBSCRIPTION_STATE_CANCELED")
            Assert.That(subscription.EndsAt, Is.EqualTo(expiry).Within(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public async Task RevokeLatestPaymentRemovesAccessAndRefundsOnlyOnce()
    {
        var service = PlayService();
        await service.SyncGooglePlay(Token, "buyer");
        remote.SubscriptionState = "SUBSCRIPTION_STATE_EXPIRED";
        remote.CanceledStateContext = new CanceledStateContext { DeveloperInitiatedCancellation = new() };
        remote.LineItems[0].ExpiryTimeDateTimeOffset = DateTimeOffset.UtcNow.AddMinutes(-1);
        publisher.Setup(p => p.IsVoidedPurchase(Order, Token)).ReturnsAsync(true);

        Assert.That(await PostGooglePlay(PlayCallback(service), SubscriptionNotification(type: 12), true), Is.TypeOf<OkResult>());
        var transactionsAfterRefund = await db.FiniteTransactions.CountAsync();
        await service.SyncGooglePlayNotification(Token, revoked: true);

        var subscription = await db.Subscriptions.SingleAsync();
        Assert.That(subscription.Status, Is.EqualTo("expired"));
        var access = await Access(subscription.ExternalId);
        Assert.That(access == null || access.Expires <= DateTime.UtcNow, Is.True);
        Assert.That((await db.PaymentRecords.SingleAsync()).Status, Is.EqualTo(PaymentRecordStatus.Refunded));
        Assert.That(await db.FiniteTransactions.CountAsync(), Is.EqualTo(transactionsAfterRefund));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task UnconfirmedRevokeSynchronizesWithoutRefund(bool expired)
    {
        var service = PlayService();
        await service.SyncGooglePlay(Token, "buyer");
        var subscription = await db.Subscriptions.SingleAsync();
        var originalExpiry = (await Access(subscription.ExternalId)).Expires;
        if (expired)
        {
            remote.SubscriptionState = "SUBSCRIPTION_STATE_EXPIRED";
            remote.CanceledStateContext = new CanceledStateContext { UserInitiatedCancellation = new() };
            remote.LineItems[0].ExpiryTimeDateTimeOffset = DateTimeOffset.UtcNow.AddMinutes(-1);
        }

        Assert.That(await PostGooglePlay(PlayCallback(service), SubscriptionNotification(type: 12), true), Is.TypeOf<OkResult>());

        Assert.That(subscription.Status, Is.EqualTo(expired ? "expired" : "active"));
        var accessExpiry = (await Access(subscription.ExternalId)).Expires;
        if (expired)
            Assert.That(accessExpiry, Is.LessThanOrEqualTo(DateTime.UtcNow));
        else
            Assert.That(accessExpiry, Is.EqualTo(originalExpiry));
        Assert.That((await db.PaymentRecords.SingleAsync()).Status, Is.EqualTo(PaymentRecordStatus.Confirmed));
        Assert.That(await db.RefundedSubscriptionInvoices.CountAsync(), Is.Zero);
        publisher.Verify(p => p.IsVoidedPurchase(Order, Token), expired ? Times.Once() : Times.Never());
    }

    [Test]
    public async Task VoidingOlderOrderPreservesCurrentPaidAccessAndIsIdempotent()
    {
        var service = PlayService();
        await service.SyncGooglePlay(Token, "buyer");
        Renew();
        await service.SyncGooglePlayNotification(Token);
        var subscription = await db.Subscriptions.SingleAsync();
        var currentExpiry = (await Access(subscription.ExternalId)).Expires;

        publisher.Setup(p => p.IsVoidedPurchase(Order, Token)).ReturnsAsync(true);
        var callback = PlayCallback(service);
        Assert.That(await PostGooglePlay(callback, VoidedNotification(productType: 1), true), Is.TypeOf<OkResult>());
        var count = await db.FiniteTransactions.CountAsync();
        Assert.That(await PostGooglePlay(callback, VoidedNotification(productType: 1), false), Is.TypeOf<OkResult>());

        Assert.That((await Access(subscription.ExternalId)).Expires, Is.EqualTo(currentExpiry));
        Assert.That((await db.PaymentRecords.SingleAsync(p => p.ExternalOrderId == Order)).Status, Is.EqualTo(PaymentRecordStatus.Refunded));
        Assert.That((await db.PaymentRecords.SingleAsync(p => p.ExternalOrderId != Order)).Status, Is.EqualTo(PaymentRecordStatus.Confirmed));
        Assert.That(await db.FiniteTransactions.CountAsync(), Is.EqualTo(count));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task LatestOrderVoidExpiresOnlyWhenConfirmedAndRenewalRestoresAccess(bool confirmed)
    {
        var service = PlayService();
        await service.SyncGooglePlay(Token, "buyer");
        var subscription = await db.Subscriptions.SingleAsync();
        var originalExpiry = (await Access(subscription.ExternalId)).Expires;
        publisher.Setup(p => p.IsVoidedPurchase(Order, Token)).ReturnsAsync(confirmed);
        var callback = PlayCallback(service);

        Assert.That(await PostGooglePlay(callback, VoidedNotification(productType: 1), true), Is.TypeOf<OkResult>());

        Assert.That(subscription.Status, Is.EqualTo(confirmed ? "expired" : "active"));
        Assert.That((await db.PaymentRecords.SingleAsync()).Status,
            Is.EqualTo(confirmed ? PaymentRecordStatus.Refunded : PaymentRecordStatus.Confirmed));
        if (!confirmed)
        {
            Assert.That((await Access(subscription.ExternalId)).Expires, Is.EqualTo(originalExpiry));
            return;
        }
        await service.SyncGooglePlayNotification(Token);
        Assert.That(subscription.Status, Is.EqualTo("expired"));
        Assert.That((await Access(subscription.ExternalId)).Expires, Is.LessThanOrEqualTo(DateTime.UtcNow));
        expiry = DateTime.UtcNow;
        Renew();
        await service.SyncGooglePlayNotification(Token);
        Assert.That(subscription.Status, Is.EqualTo("active"));
        Assert.That((await Access(subscription.ExternalId)).Expires, Is.InRange(expiry, expiry.AddHours(3)));
    }

    [Test]
    public async Task LinkedPurchaseInheritsItsUserAndClosesThePreviousToken()
    {
        var service = PlayService();
        await service.SyncGooglePlay(Token, "buyer");
        var previous = await db.Subscriptions.SingleAsync();
        remote.LinkedPurchaseToken = Token;
        remote.ExternalAccountIdentifiers = null;
        remote.LineItems[0].LatestSuccessfulOrderId = "GPA.9876-5432-1098-76543";

        Assert.That((await service.SyncGooglePlayNotification("replacement-token")).IsValid, Is.True);
        var replacement = await db.Subscriptions.Include(s => s.User).SingleAsync(s => s.ProviderToken == "replacement-token");
        Assert.That(replacement.User.ExternalId, Is.EqualTo("buyer"));
        Assert.That(previous.Status, Is.EqualTo("expired"));
        var previousAccess = await Access(previous.ExternalId);
        Assert.That(previousAccess == null || previousAccess.Expires <= DateTime.UtcNow, Is.True);
        Assert.That((await Access(replacement.ExternalId)).Expires, Is.InRange(expiry, expiry.AddHours(3)));
    }

    [TestCase("US")]
    [TestCase(null)]
    public async Task DisallowedOrMissingRegionDoesNotGrantAcknowledgeOrMarkRefund(string region)
    {
        remote.RegionCode = region;
        remote.AcknowledgementState = "ACKNOWLEDGEMENT_STATE_PENDING";
        Assert.That((await PlayService().SyncGooglePlay(Token, "buyer")).ErrorMessage, Is.EqualTo("region_not_allowed"));
        Assert.That(await db.OwnerShips.CountAsync(), Is.Zero);
        Assert.That(await db.FiniteTransactions.CountAsync(), Is.Zero);
        Assert.That(await db.PaymentRequests.CountAsync(), Is.Zero);
        publisher.Verify(p => p.AcknowledgeSubscription(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task AllowlistChangeDoesNotAffectTheGrantedOrderOrItsRenewal()
    {
        await PlayService().SyncGooglePlay(Token, "buyer");
        var service = PlayService(allowedRegions: "US");

        Assert.That((await service.SyncGooglePlayNotification(Token)).IsValid, Is.True);
        Renew();
        Assert.That((await service.SyncGooglePlayNotification(Token)).IsValid, Is.True);

        var subscription = await db.Subscriptions.SingleAsync();
        Assert.That(subscription.Status, Is.EqualTo("active"));
        Assert.That((await Access(subscription.ExternalId)).Expires, Is.InRange(expiry, expiry.AddHours(3)));
        Assert.That(await PaymentRecords("googleplay"), Is.EqualTo(2));
    }

    [TestCase(HttpStatusCode.BadRequest, false)]
    [TestCase(HttpStatusCode.BadRequest, true)]
    [TestCase(HttpStatusCode.NotFound, false)]
    [TestCase(HttpStatusCode.NotFound, true)]
    public async Task UnavailableRenewalPreservesTheRowAndPaidAccessAndRedeliveryGrantsItOnce(HttpStatusCode status, bool cancelled)
    {
        expiry = DateTime.UtcNow.AddMinutes(-1);
        remote.LineItems[0].ExpiryTimeDateTimeOffset = new DateTimeOffset(expiry);
        var service = PlayService();
        await service.SyncGooglePlay(Token, "buyer");
        var subscription = await db.Subscriptions.SingleAsync();
        subscription.UpdatedAt = DateTime.UtcNow.AddHours(-7);
        if (cancelled)
        {
            subscription.Status = "cancelled";
            subscription.EndsAt = expiry;
        }
        await db.SaveChangesAsync();
        var originalExpiry = (await Access(subscription.ExternalId)).Expires;
        var lastSync = subscription.UpdatedAt;
        var cutoff = DateTime.UtcNow.AddHours(-6);
        publisher.Setup(p => p.GetSubscriptionV2(Token)).ThrowsAsync(new GoogleApiException("androidpublisher") { HttpStatusCode = status });

        var result = await PostGooglePlay(PlayCallback(service), SubscriptionNotification(type: 2), true);

        Assert.That(((StatusCodeResult)result).StatusCode, Is.EqualTo(500));
        Assert.ThrowsAsync<GoogleApiException>(() => service.ReconcileGooglePlay(subscription.ExternalId, cutoff));
        var persisted = await db.Subscriptions.AsNoTracking().SingleAsync();
        Assert.That(persisted.Status, Is.EqualTo(cancelled ? "cancelled" : "active"));
        Assert.That(persisted.EndsAt, Is.EqualTo(cancelled ? expiry : (DateTime?)null));
        Assert.That(persisted.RenewsAt, Is.EqualTo(expiry));
        Assert.That(persisted.UpdatedAt, Is.EqualTo(lastSync));
        Assert.That((await Access(subscription.ExternalId)).Expires, Is.EqualTo(originalExpiry));

        Renew();
        var renewalOrder = remote.LineItems[0].LatestSuccessfulOrderId;
        publisher.Setup(p => p.GetSubscriptionV2(Token)).ReturnsAsync(remote);
        var callback = PlayCallback(service);
        Assert.That(await PostGooglePlay(callback, SubscriptionNotification(type: 2), true), Is.TypeOf<OkResult>());
        Assert.That(await PostGooglePlay(callback, SubscriptionNotification(type: 2), false), Is.TypeOf<OkResult>());

        Assert.That(subscription.Status, Is.EqualTo("active"));
        Assert.That(subscription.EndsAt, Is.Null);
        Assert.That(await Grants(SubscriptionProviders.GrantReference("googleplay", renewalOrder)), Is.EqualTo(1));
        Assert.That(await PaymentRecords("googleplay"), Is.EqualTo(2));
        Assert.That(await db.PaymentRecords.CountAsync(p => p.ExternalOrderId == renewalOrder), Is.EqualTo(1));
        Assert.That((await Access(subscription.ExternalId)).Expires, Is.InRange(expiry, expiry.AddHours(3)));
    }

    [Test]
    public async Task UnavailablePurchaseOnHoldWithPastExpiryRemainsOnHoldAndRetries()
    {
        var service = PlayService();
        await service.SyncGooglePlay(Token, "buyer");
        remote.SubscriptionState = "SUBSCRIPTION_STATE_ON_HOLD";
        remote.LineItems[0].ExpiryTimeDateTimeOffset = DateTimeOffset.UtcNow.AddMinutes(-1);
        await service.SyncGooglePlayNotification(Token);
        publisher.Setup(p => p.GetSubscriptionV2(Token)).ThrowsAsync(new GoogleApiException("androidpublisher") { HttpStatusCode = HttpStatusCode.NotFound });

        var result = await PostGooglePlay(PlayCallback(service), SubscriptionNotification(type: 5), true);

        Assert.That(((StatusCodeResult)result).StatusCode, Is.EqualTo(500));
        var persisted = await db.Subscriptions.AsNoTracking().SingleAsync();
        Assert.That(persisted.Status, Is.EqualTo("unpaid"));
        Assert.That(persisted.EndsAt, Is.Null);
        Assert.That(persisted.RenewsAt, Is.LessThan(DateTime.UtcNow));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task GonePublisherResponseExpiresTheRowWithoutChangingPaidAccess(bool cancelled)
    {
        var service = PlayService();
        await service.SyncGooglePlay(Token, "buyer");
        var subscription = await db.Subscriptions.SingleAsync();
        var originalExpiry = (await Access(subscription.ExternalId)).Expires;
        var past = DateTime.UtcNow.AddDays(-1);
        if (cancelled)
        {
            subscription.Status = "cancelled";
            subscription.EndsAt = past;
        }
        await db.SaveChangesAsync();
        publisher.Setup(p => p.GetSubscriptionV2(Token)).ThrowsAsync(new GoogleApiException("androidpublisher") { HttpStatusCode = HttpStatusCode.Gone });
        var cutoff = DateTime.UtcNow.AddSeconds(1);

        await service.ReconcileGooglePlay(subscription.ExternalId, cutoff);

        Assert.That(subscription.Status, Is.EqualTo("expired"));
        Assert.That(subscription.EndsAt, Is.EqualTo(cancelled ? past : DateTime.UtcNow).Within(TimeSpan.FromSeconds(5)));
        Assert.That((await Access(subscription.ExternalId)).Expires, Is.EqualTo(originalExpiry));
        Assert.That((await service.GooglePlayEligibility("buyer", "l_premium")).Eligible, Is.True);
        publisher.Invocations.Clear();
        await service.ReconcileGooglePlay(subscription.ExternalId, cutoff);
        publisher.Verify(p => p.GetSubscriptionV2(Token), Times.Never);
    }

    [Test]
    public async Task TestPurchaseGrantsAccessWithoutCreatingRealPaymentRecord()
    {
        remote.TestPurchase = new TestPurchase();
        Assert.That((await PlayService().SyncGooglePlay(Token, "buyer")).IsValid, Is.True);
        Assert.That(await db.OwnerShips.CountAsync(), Is.EqualTo(1));
        Assert.That(await PaymentRecords("googleplay"), Is.Zero);
    }

    [Test]
    public async Task TestPurchasesCanBeDisabled()
    {
        remote.TestPurchase = new TestPurchase();
        Assert.That((await PlayService(acceptTestPurchases: false).SyncGooglePlay(Token, "buyer")).IsValid, Is.False);
        Assert.That(await db.Subscriptions.CountAsync(), Is.Zero);
        Assert.That(await PaymentRecords("googleplay"), Is.Zero);
    }

    [Test]
    public async Task PaidOverlapStillGrantsAndRecordsTheRaceOnce()
    {
        db.Subscriptions.Add(new UserSubscription
        {
            ProviderSlug = "stripe", Status = "active", Product = premiumPlus, User = await users.GetOrCreate("buyer"), ExternalId = "sub_web"
        });
        await db.SaveChangesAsync();
        var before = PaymentMetrics.GooglePlayDoubleSubscriptions.Value;
        var service = PlayService();
        Assert.That((await service.SyncGooglePlay(Token, "buyer")).IsValid, Is.True);
        await service.SyncGooglePlayNotification(Token);

        Assert.That(await db.OwnerShips.CountAsync(), Is.EqualTo(1));
        Assert.That(PaymentMetrics.GooglePlayDoubleSubscriptions.Value, Is.EqualTo(before + 1));
    }

    [Test]
    public async Task ListedGoogleSubscriptionAdvertisesExternalManagement()
    {
        var service = PlayService();
        await service.SyncGooglePlay(Token, "buyer");
        var subscription = (await service.GetUserSubscriptions("buyer")).Single();
        Assert.That(subscription.CanResume, Is.False);
        Assert.That(subscription.CanChangePlan, Is.False);
        Assert.That(subscription.CanExtendLongTerm, Is.False);
        Assert.That(subscription.ManageUrl, Does.StartWith("https://play.google.com/store/account/subscriptions"));
        Assert.That(subscription.ManageUrl, Does.Contain("com.coflnet.sky"));
        Assert.That(subscription.ManageUrl, Does.Not.Contain(Token));
        Assert.That(JsonConvert.SerializeObject(subscription, new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore }),
            Does.Not.Contain(Token));
        Assert.That((await service.GetSubscriptionInvoices("buyer", subscription.ExternalId)).Single().Id, Is.EqualTo(Order));
        Assert.That(await service.GetAvailablePlans("buyer", subscription.ExternalId), Is.Empty);
        Assert.That(Assert.ThrowsAsync<ApiException>(() => service.CancelSubscription("buyer", subscription.ExternalId)).Message,
            Is.EqualTo("Managed in Google Play"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task WebhookAcceptsPubSubAndUnwrappedNotifications(bool wrapped)
    {
        var callback = PlayCallback(PlayService());
        Assert.That(await PostGooglePlay(callback, SubscriptionNotification(), wrapped), Is.TypeOf<OkResult>());
        Assert.That(await db.Subscriptions.CountAsync(), Is.EqualTo(1));
        Assert.That(await PaymentRecords("googleplay"), Is.EqualTo(1));
    }

    [Test]
    public async Task ConfiguredPushRejectsInvalidOidc()
    {
        var callback = Callback(PlayService(), new Dictionary<string, string>
        {
            ["GOOGLEPAY:PUBSUB_AUDIENCE"] = Audience,
            ["GOOGLEPAY:PUBSUB_SERVICE_ACCOUNT"] = PushAccount
        });
        callback.Request.Headers.Authorization = "Bearer invalid-jwt";
        Assert.That(await PostGooglePlay(callback, SubscriptionNotification(), true), Is.TypeOf<UnauthorizedResult>());
        publisher.Verify(p => p.GetSubscriptionV2(It.IsAny<string>()), Times.Never);
    }

    [TestCase(null, null)]
    [TestCase(null, PushAccount)]
    [TestCase(Audience, null)]
    public async Task PushRejectsIncompleteOidcConfiguration(string audience, string account)
    {
        var callback = PlayCallback(PlayService(), audience, account);
        callback.ValidateGooglePlayPushToken = _ => throw new InvalidOperationException("Missing config must reject before validation");

        Assert.That(await PostGooglePlay(callback, SubscriptionNotification(), true), Is.TypeOf<UnauthorizedResult>());
        publisher.Verify(p => p.GetSubscriptionV2(It.IsAny<string>()), Times.Never);
    }

    [TestCase("wrong-audience", PushAccount, true, "https://accounts.google.com")]
    [TestCase(Audience, "wrong@example.com", true, "https://accounts.google.com")]
    [TestCase(Audience, PushAccount, false, "https://accounts.google.com")]
    [TestCase(Audience, PushAccount, true, "https://wrong.example.com")]
    public async Task PushRejectsIncorrectOidcClaims(string audience, string account, bool verified, string issuer)
    {
        var callback = PlayCallback(PlayService());
        callback.ValidateGooglePlayPushToken = _ => Task.FromResult(PlayPushClaims(audience, account, verified, issuer));

        Assert.That(await PostGooglePlay(callback, SubscriptionNotification(), true), Is.TypeOf<UnauthorizedResult>());
        publisher.Verify(p => p.GetSubscriptionV2(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task PublisherFailureReturnsRetryableWebhookStatus()
    {
        publisher.Setup(p => p.GetSubscriptionV2(Token)).ThrowsAsync(new TimeoutException());
        var result = await PostGooglePlay(PlayCallback(PlayService()), SubscriptionNotification(), true);
        Assert.That(((StatusCodeResult)result).StatusCode, Is.EqualTo(500));
    }

    [TestCase(HttpStatusCode.BadRequest)]
    [TestCase(HttpStatusCode.NotFound)]
    public async Task UnavailableNewPurchaseReturnsRetryableCompletionStatus(HttpStatusCode status)
    {
        publisher.Setup(p => p.GetSubscriptionV2(Token)).ThrowsAsync(new GoogleApiException("androidpublisher") { HttpStatusCode = status });

        var result = (ObjectResult)(await CompletePurchase(PlayService())).Result;

        Assert.That(result.StatusCode, Is.EqualTo(500));
        Assert.That(result.Value, Is.EqualTo(new GooglePlaySubscriptionResult(false, null, "verification_unavailable")));
        Assert.That(await db.Subscriptions.CountAsync(), Is.Zero);
        Assert.That(await db.OwnerShips.CountAsync(), Is.Zero);
        Assert.That(await PaymentRecords("googleplay"), Is.Zero);
    }

    [TestCase(0, true)]
    [TestCase(2, false)]
    public async Task OnlyExplicitPartialRefundSkipsConfirmationAndReversal(int refundType, bool reversed)
    {
        var service = PlayService();
        await service.SyncGooglePlay(Token, "buyer");
        publisher.Setup(p => p.IsVoidedPurchase(Order, Token)).ReturnsAsync(true);
        var notification = VoidedNotification(productType: 1);
        notification.VoidedPurchaseNotification.RefundType = refundType;
        notification.EventTimeMillis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Assert.That(await PostGooglePlay(PlayCallback(service), notification, true), Is.TypeOf<OkResult>());

        publisher.Verify(p => p.IsVoidedPurchase(Order, Token), reversed ? Times.Once() : Times.Never());
        Assert.That((await db.Subscriptions.AsNoTracking().SingleAsync()).Status, Is.EqualTo(reversed ? "expired" : "active"));
        Assert.That((await db.PaymentRecords.SingleAsync()).Status,
            Is.EqualTo(reversed ? PaymentRecordStatus.Refunded : PaymentRecordStatus.Confirmed));
    }

    [TestCase(null, false, false)]
    [TestCase(-25 * 60, false, false)]
    [TestCase(-60, false, true)]
    [TestCase(5, false, true)]
    [TestCase(6, false, false)]
    [TestCase(null, true, true)]
    [TestCase(-25 * 60, true, true)]
    [TestCase(-60, true, true)]
    [TestCase(5, true, true)]
    [TestCase(6, true, true)]
    public async Task VoidedOneTimePurchaseAlwaysRetriesListFailuresAndOnlyRecentConfirmationMisses(int? offsetMinutes, bool listFails, bool retry)
    {
        var topUp = new TopUpProduct
        {
            Slug = "coins", ProviderSlug = "googlepay", Type = Product.ProductType.TOP_UP, Cost = 150, Price = 1m
        };
        db.TopUpProducts.Add(topUp);
        await db.SaveChangesAsync();
        await users.GetOrCreate("buyer");
        await transactions.AddTopUp(topUp.Id, "buyer", Order, 150);
        var callback = PlayCallback(PlayService());
        var notification = VoidedNotification(productType: 2);
        if (offsetMinutes is int minutes)
            notification.EventTimeMillis = DateTimeOffset.UtcNow.AddMinutes(minutes).ToUnixTimeMilliseconds();
        if (listFails)
            publisher.Setup(p => p.IsVoidedPurchase(Order, Token)).ThrowsAsync(
                new GoogleApiException("androidpublisher") { HttpStatusCode = HttpStatusCode.TooManyRequests });

        var result = await PostGooglePlay(callback, notification, true);

        if (retry)
            Assert.That(((StatusCodeResult)result).StatusCode, Is.EqualTo(500));
        else
            Assert.That(result, Is.TypeOf<OkResult>());
        Assert.That((await Buyer()).Balance, Is.EqualTo(150));

        publisher.Setup(p => p.IsVoidedPurchase(Order, Token)).ReturnsAsync(true);
        Assert.That(await PostGooglePlay(callback, notification, true), Is.TypeOf<OkResult>());
        Assert.That(await PostGooglePlay(callback, notification, false), Is.TypeOf<OkResult>());
        Assert.That((await Buyer()).Balance, Is.Zero);
        Assert.That(await db.FiniteTransactions.CountAsync(), Is.EqualTo(2));
        publisher.Verify(p => p.IsVoidedPurchase(Order, Token), Times.Exactly(3));
        Assert.That(WebhookLogSummary.OfGooglePlay(notification), Does.Not.Contain(Token).And.Not.Contain(Order));
    }

    [Test]
    public async Task EligibilityBlocksRenewingWebSubscriptionAndAllowsAfterCancellation()
    {
        var row = new UserSubscription
        {
            ProviderSlug = SubscriptionProviders.Stripe, Status = "active", User = await users.GetOrCreate("buyer"), Product = premiumPlus, ExternalId = "sub_web"
        };
        db.Subscriptions.Add(row);
        await db.SaveChangesAsync();
        var controller = new GooglePlaySubscriptionController(PlayService());
        var blocked = await controller.Eligibility("buyer", "l_premium");
        Assert.That(blocked.Eligible, Is.False);
        Assert.That(blocked.BlockingProvider, Is.EqualTo("stripe"));
        row.EndsAt = DateTime.UtcNow.AddDays(2);
        await db.SaveChangesAsync();
        var allowed = await controller.Eligibility("buyer", "l_premium");
        Assert.That(allowed.Eligible, Is.True);
        Assert.That(allowed.PlayProductId, Is.EqualTo("premium"));
        Assert.That(allowed.BasePlanId, Is.EqualTo("p4w"));
    }

    [Test]
    public async Task GraceExtendsTheExistingGrantWithoutGrantingAgain()
    {
        var service = PlayService();
        await service.SyncGooglePlay(Token, "buyer");
        var subscription = await db.Subscriptions.SingleAsync();
        var later = (await Access(subscription.ExternalId)).Expires.AddDays(3);
        remote.LineItems[0].ExpiryTimeDateTimeOffset = later;
        remote.SubscriptionState = "SUBSCRIPTION_STATE_IN_GRACE_PERIOD";

        await service.SyncGooglePlayNotification(Token);

        Assert.That(await PaymentRecords("googleplay"), Is.EqualTo(1));
        Assert.That((await Access(subscription.ExternalId)).Expires, Is.EqualTo(later));
    }

    [Test]
    public async Task AcknowledgementFailureCanBeReconciledWithoutGrantingTwice()
    {
        remote.AcknowledgementState = "ACKNOWLEDGEMENT_STATE_PENDING";
        publisher.SetupSequence(p => p.AcknowledgeSubscription("premium", Token))
            .ThrowsAsync(new TimeoutException()).Returns(Task.CompletedTask);
        var service = PlayService();
        Assert.ThrowsAsync<TimeoutException>(() => service.SyncGooglePlay(Token, "buyer"));
        var subscription = await db.Subscriptions.SingleAsync();
        var cutoff = DateTime.UtcNow.AddHours(-5);
        subscription.UpdatedAt = cutoff.AddHours(-1);
        await db.SaveChangesAsync();

        await service.ReconcileGooglePlay(subscription.ExternalId, cutoff);
        await service.ReconcileGooglePlay(subscription.ExternalId, cutoff);

        publisher.Verify(p => p.AcknowledgeSubscription("premium", Token), Times.Exactly(2));
        Assert.That(await PaymentRecords("googleplay"), Is.EqualTo(1));
        Assert.That(await Grants(SubscriptionProviders.GrantReference("googleplay", Order)), Is.EqualTo(1));
    }

    private static GooglePlayNotification SubscriptionNotification(int type = 4) => new()
    {
        PackageName = "com.coflnet.sky",
        SubscriptionNotification = new() { PurchaseToken = Token, SubscriptionId = "premium", NotificationType = type }
    };

    private static GooglePlayNotification VoidedNotification(int productType) => new()
    {
        PackageName = "com.coflnet.sky",
        VoidedPurchaseNotification = new() { PurchaseToken = Token, OrderId = Order, ProductType = productType, RefundType = 1 }
    };

    private static Task<IActionResult> PostGooglePlay(CallbackController callback, GooglePlayNotification notification, bool wrapped)
    {
        var json = JsonConvert.SerializeObject(notification, new JsonSerializerSettings { DefaultValueHandling = DefaultValueHandling.Ignore });
        if (wrapped)
            json = JsonConvert.SerializeObject(new { message = new { data = Convert.ToBase64String(Encoding.UTF8.GetBytes(json)), messageId = "1" } });
        callback.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return callback.GooglePlayWebhook();
    }
}
