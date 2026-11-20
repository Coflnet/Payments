using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Stripe;
using PaymentRecord = Coflnet.Payments.Models.PaymentRecord;
using Keys = Coflnet.Payments.Services.SubscriptionService.StripeMetadata;

namespace Coflnet.Payments.Services;

/// <summary>
/// Chargebacks: a disputed subscription payment is reverted like a full refund and the subscription cancelled, a disputed
/// top-up loses its coins, and winning gives both back once.
/// </summary>
public class StripeDisputeTests : DirectSubscriptionTestBase
{
    private Mock<IStripeSubscriptionGateway> stripe;
    private StripeSubscriptionSnapshot remote;
    private DateTime periodEnd;
    private SubscriptionService service;

    [SetUp]
    public void SetUpStripe()
    {
        periodEnd = DateTime.UtcNow.AddDays(28);
        remote = Snapshot("active");
        stripe = new Mock<IStripeSubscriptionGateway>();
        stripe.Setup(s => s.GetSubscription("sub_1")).ReturnsAsync(() => remote);
        stripe.Setup(s => s.CancelNow("sub_1")).ReturnsAsync(() => remote = Snapshot("canceled"));
        stripe.Setup(s => s.FindInvoiceByPaymentIntent("pi_1")).ReturnsAsync(Invoice());
        service = Service(stripe.Object);
    }

    private StripeSubscriptionSnapshot Snapshot(string status) => new("sub_1", status, false, null, periodEnd,
        status == "canceled" ? DateTime.UtcNow : null, "cus_1", new Dictionary<string, string>
        {
            [Keys.UserId] = "buyer", [Keys.ProductSlug] = "l_premium", [Keys.PriceCents] = "969", [Keys.Mode] = "direct", [Keys.Country] = "US"
        }, "si_1", "price_1", "prod_checkout", 969, "eur", null, false, DateTime.UtcNow, false);

    private static StripeInvoiceInfo Invoice() =>
        new("in_1", "sub_1", "subscription_create", "paid", 969, 969, "eur", DateTime.UtcNow, "DE", "10115", "buyer@example.test",
            "Buyer", new Dictionary<string, string>());

    private static Event DisputeEvent(string type, string status, string paymentIntent = "pi_1") => new()
    {
        Id = "evt_" + type, Type = type,
        Data = new EventData { Object = new Dispute { Id = "dp_1", PaymentIntentId = paymentIntent, Status = status, Amount = 969, Currency = "eur" } }
    };

    private Task<bool> Handle(string type, string status = "needs_response", string paymentIntent = "pi_1") =>
        Callback(service).HandleStripeDisputeEvent(DisputeEvent(type, status, paymentIntent));

    private async Task<PaymentRecord> SeedRecord(string orderId, PaymentRecordStatus status = PaymentRecordStatus.Confirmed)
    {
        var record = new PaymentRecord
        {
            UserId = (await users.GetOrCreate("buyer")).Id, ExternalUserId = "buyer", Provider = "stripe", ExternalOrderId = orderId,
            GrossAmount = 9.69m, Currency = "EUR", Status = status, IsSubscriptionPayment = true
        };
        db.PaymentRecords.Add(record);
        await db.SaveChangesAsync();
        return record;
    }

    private async Task<PaymentRecordStatus> RecordStatus(string orderId) =>
        (await db.PaymentRecords.AsNoTracking().SingleAsync(r => r.ExternalOrderId == orderId)).Status;

    private Task<int> Reverts() => db.FiniteTransactions.CountAsync(t => t.Reference.StartsWith("revert transaction"));

    private async Task PaidSubscription()
    {
        Assert.That(await service.StripeInvoicePaid(Invoice()), Is.Not.Null);
        await SeedRecord("in_1");
    }

    [Test]
    public async Task DisputeOfASubscriptionInvoiceRevertsItAndCancelsTheSubscriptionImmediately()
    {
        await PaidSubscription();

        Assert.That(await Handle(EventTypes.ChargeDisputeCreated), Is.True);

        Assert.That(await db.RefundedSubscriptionInvoices.CountAsync(i => i.InvoiceId == "in_1"), Is.EqualTo(1));
        Assert.That(await Reverts(), Is.EqualTo(2), "the period and its coin credit");
        Assert.That((await Access("sub_1")).Expires, Is.LessThan(DateTime.UtcNow.AddMinutes(1)));
        stripe.Verify(s => s.CancelNow("sub_1"), Times.Once);
        Assert.That((await Subscription("sub_1")).Status, Is.EqualTo("expired"));
        Assert.That(await RecordStatus("in_1"), Is.EqualTo(PaymentRecordStatus.Disputed));
    }

    [Test]
    public async Task DisputeReleasesTheRenewalScheduleBeforeCancelling()
    {
        await PaidSubscription();
        var subscription = await Subscription("sub_1");
        subscription.RenewalScheduleId = "sub_sched_1";
        await db.SaveChangesAsync();
        remote = Snapshot("active") with { ScheduleId = "sub_sched_1" };
        stripe.Setup(s => s.ReleaseSchedule("sub_sched_1", "sub_1")).ReturnsAsync(() => remote = Snapshot("active"));

        await Handle(EventTypes.ChargeDisputeCreated);

        stripe.Verify(s => s.ReleaseSchedule("sub_sched_1", "sub_1"), Times.Once);
        stripe.Verify(s => s.CancelNow("sub_1"), Times.Once);
        Assert.That((await Subscription("sub_1")).RenewalScheduleId, Is.Null);
    }

    [Test]
    public async Task DuplicateDeliveryAndTheFundsWithdrawnFollowUpAreIdempotent()
    {
        await PaidSubscription();
        var opened = PaymentMetrics.Disputes.WithLabels("stripe", "opened").Value;

        await Handle(EventTypes.ChargeDisputeCreated);
        await Handle(EventTypes.ChargeDisputeCreated);
        await Handle(EventTypes.ChargeDisputeFundsWithdrawn);

        Assert.That(PaymentMetrics.Disputes.WithLabels("stripe", "opened").Value, Is.EqualTo(opened + 1), "counted when it reverted, once");
        Assert.That(await db.RefundedSubscriptionInvoices.CountAsync(), Is.EqualTo(1));
        Assert.That(await Reverts(), Is.EqualTo(2));
        stripe.Verify(s => s.CancelNow("sub_1"), Times.Once);
    }

    [Test]
    public async Task DisputeAfterAFullRefundDoesNothingTwice()
    {
        await PaidSubscription();
        await service.StripeInvoiceRefunded(Invoice(), 969, 969);
        (await db.PaymentRecords.SingleAsync()).Status = PaymentRecordStatus.Refunded;
        await db.SaveChangesAsync();
        var reverts = await Reverts();

        await Handle(EventTypes.ChargeDisputeCreated);

        Assert.That(await Reverts(), Is.EqualTo(reverts));
        Assert.That(await db.RefundedSubscriptionInvoices.CountAsync(), Is.EqualTo(1));
        stripe.Verify(s => s.CancelNow(It.IsAny<string>()), Times.Never);
        Assert.That(await RecordStatus("in_1"), Is.EqualTo(PaymentRecordStatus.Refunded));
    }

    [Test]
    public async Task DisputeWonRestoresThePeriodOnceAndKeepsTheSubscriptionCancelled()
    {
        await PaidSubscription();
        await Handle(EventTypes.ChargeDisputeCreated);

        await Handle(EventTypes.ChargeDisputeClosed, "won");
        await Handle(EventTypes.ChargeDisputeClosed, "won");

        Assert.That(await Grants("stripe-invoice-in_1-disputewon"), Is.EqualTo(1));
        Assert.That(await Grants("stripe-invoice-in_1-disputewon-topup"), Is.EqualTo(1));
        Assert.That((await Access("sub_1")).Expires, Is.GreaterThan(DateTime.UtcNow.AddDays(20)));
        Assert.That(await db.RefundedSubscriptionInvoices.CountAsync(), Is.Zero);
        Assert.That(await RecordStatus("in_1"), Is.EqualTo(PaymentRecordStatus.Confirmed));
        Assert.That((await Subscription("sub_1")).Status, Is.EqualTo("expired"), "not resumed");
        stripe.Verify(s => s.CancelNow("sub_1"), Times.Once);
        // A late duplicate of the opening event must not take the period away again.
        await Handle(EventTypes.ChargeDisputeFundsWithdrawn);
        Assert.That((await Access("sub_1")).Expires, Is.GreaterThan(DateTime.UtcNow.AddDays(20)));
        Assert.That(await RecordStatus("in_1"), Is.EqualTo(PaymentRecordStatus.Confirmed));
    }

    [Test]
    public async Task DisputeLostKeepsEverythingReverted()
    {
        await PaidSubscription();
        await Handle(EventTypes.ChargeDisputeCreated);

        await Handle(EventTypes.ChargeDisputeClosed, "lost");

        Assert.That(await Grants("stripe-invoice-in_1-disputewon"), Is.Zero);
        Assert.That((await Access("sub_1")).Expires, Is.LessThan(DateTime.UtcNow.AddMinutes(1)));
        Assert.That(await RecordStatus("in_1"), Is.EqualTo(PaymentRecordStatus.Disputed));
    }

    /// <summary>The ledger, not the best-effort PaymentRecord, knows the buyer was refunded: no record exists here at all</summary>
    [Test]
    public async Task WonDisputeAfterARefundMeanwhileDoesNotRestoreEvenWithoutARecord()
    {
        Assert.That(await service.StripeInvoicePaid(Invoice()), Is.Not.Null);
        await Handle(EventTypes.ChargeDisputeCreated);
        await service.StripeInvoiceRefunded(Invoice(), 969, 969);

        await Handle(EventTypes.ChargeDisputeClosed, "won");

        Assert.That(await Grants("stripe-invoice-in_1-disputewon"), Is.Zero);
        Assert.That((await Access("sub_1")).Expires, Is.LessThan(DateTime.UtcNow.AddMinutes(1)));
    }

    [Test]
    public async Task WonDisputeOfAnInvoiceAnEarlierRefundRevertedDoesNotRestore()
    {
        Assert.That(await service.StripeInvoicePaid(Invoice()), Is.Not.Null);
        await service.StripeInvoiceRefunded(Invoice(), 969, 969);
        await Handle(EventTypes.ChargeDisputeCreated);

        await Handle(EventTypes.ChargeDisputeClosed, "won");

        Assert.That(await Grants("stripe-invoice-in_1-disputewon"), Is.Zero);
        stripe.Verify(s => s.CancelNow(It.IsAny<string>()), Times.Never);
    }

    /// <summary>Regression: the revert of the original grant is idempotent, so a refund after a won dispute left the re-grant standing</summary>
    [Test]
    public async Task RefundAfterAWonDisputeAlsoRevertsTheRegrantedPeriod()
    {
        await PaidSubscription();
        await Handle(EventTypes.ChargeDisputeCreated);
        await Handle(EventTypes.ChargeDisputeClosed, "won");
        Assert.That((await Access("sub_1")).Expires, Is.GreaterThan(DateTime.UtcNow.AddDays(20)));

        await service.StripeInvoiceRefunded(Invoice(), 969, 969);

        Assert.That((await Access("sub_1")).Expires, Is.LessThan(DateTime.UtcNow.AddMinutes(1)));
        Assert.That(await db.FiniteTransactions.CountAsync(t => t.Reference.StartsWith("revert transaction")), Is.EqualTo(4));
    }

    [Test]
    public async Task OpeningEventOfADisputeStripeReportsAsWonIsIgnored()
    {
        await PaidSubscription();
        stripe.Setup(s => s.GetDisputeStatus("dp_1")).ReturnsAsync("won");

        await Handle(EventTypes.ChargeDisputeFundsWithdrawn);

        Assert.That(await db.RefundedSubscriptionInvoices.CountAsync(), Is.Zero);
        stripe.Verify(s => s.CancelNow(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task InquiryRevertsNothing()
    {
        await PaidSubscription();

        Assert.That(await Handle(EventTypes.ChargeDisputeCreated, "warning_needs_response"), Is.True);
        Assert.That(await Handle(EventTypes.ChargeDisputeClosed, "warning_closed"), Is.True);

        Assert.That(await db.RefundedSubscriptionInvoices.CountAsync(), Is.Zero);
        Assert.That((await Access("sub_1")).Expires, Is.GreaterThan(DateTime.UtcNow.AddDays(20)));
        stripe.Verify(s => s.CancelNow(It.IsAny<string>()), Times.Never);
    }

    private async Task<TopUpProduct> SeedTopUp(string reference)
    {
        var topUp = new TopUpProduct { Slug = "topup-stripe", Title = "Top up", Cost = 1800, ProviderSlug = "stripe", Type = Coflnet.Payments.Models.Product.ProductType.TOP_UP };
        db.TopUpProducts.Add(topUp);
        await db.SaveChangesAsync();
        await users.GetOrCreate("buyer");
        await transactions.AddTopUp(topUp.Id, "buyer", reference);
        await SeedRecord("cs_1");
        (await db.PaymentRecords.SingleAsync()).ExternalTransactionId = reference;
        await db.SaveChangesAsync();
        return topUp;
    }

    [Test]
    public async Task DisputeOfATopUpTakesItsCoinsBackAndWinningGivesThemBackOnce()
    {
        await SeedTopUp("pi_topup");
        Assert.That((await Buyer()).Balance, Is.EqualTo(1800));

        Assert.That(await Handle(EventTypes.ChargeDisputeCreated, paymentIntent: "pi_topup"), Is.True);
        await Handle(EventTypes.ChargeDisputeFundsWithdrawn, paymentIntent: "pi_topup");

        Assert.That((await Buyer()).Balance, Is.Zero);
        Assert.That(await db.PaymentRecords.AsNoTracking().SingleAsync(), Has.Property(nameof(PaymentRecord.Status)).EqualTo(PaymentRecordStatus.Disputed));

        await Handle(EventTypes.ChargeDisputeClosed, "won", "pi_topup");
        await Handle(EventTypes.ChargeDisputeClosed, "won", "pi_topup");

        Assert.That((await Buyer()).Balance, Is.EqualTo(1800));
        Assert.That(await db.PaymentRecords.AsNoTracking().SingleAsync(), Has.Property(nameof(PaymentRecord.Status)).EqualTo(PaymentRecordStatus.Confirmed));
    }

    [Test]
    public async Task DisputeOfARefundedTopUpDeductsNothingMore()
    {
        await SeedTopUp("pi_topup");
        await transactions.ApplyTopUpRefund("pi_topup", 1800, 1800, "stripe");
        Assert.That((await Buyer()).Balance, Is.Zero);

        await Handle(EventTypes.ChargeDisputeCreated, paymentIntent: "pi_topup");

        Assert.That((await Buyer()).Balance, Is.Zero, "not deducted twice");
    }

    [Test]
    public async Task WonTopUpDisputeAfterARefundDoesNotGiveTheCoinsBack()
    {
        await SeedTopUp("pi_topup");
        await Handle(EventTypes.ChargeDisputeCreated, paymentIntent: "pi_topup");
        await transactions.ApplyTopUpRefund("pi_topup", 1800, 1800, "stripe");

        await Handle(EventTypes.ChargeDisputeClosed, "won", "pi_topup");

        Assert.That((await Buyer()).Balance, Is.Zero);
    }

    [Test]
    public async Task DisputeOfAnUnknownChargeIsIgnoredWithoutFailing()
    {
        Assert.That(await Handle(EventTypes.ChargeDisputeCreated, paymentIntent: "pi_unknown"), Is.True);
        Assert.That(await Handle(EventTypes.ChargeDisputeClosed, "won", "pi_unknown"), Is.True);
        Assert.That(await Handle(EventTypes.ChargeDisputeCreated, paymentIntent: null), Is.True);
        Assert.That(await db.FiniteTransactions.CountAsync(), Is.Zero);
    }

    /// <summary>A dispute of another account makes Stripe answer 404: the event's own status decides and the answer is 200</summary>
    [Test]
    public async Task ForeignDisputeStripeDoesNotKnowIsAnsweredWithoutFailing()
    {
        stripe.Setup(s => s.GetDisputeStatus("dp_1")).ThrowsAsync(new StripeException(System.Net.HttpStatusCode.NotFound, null, "no such dispute"));

        Assert.That(await Handle(EventTypes.ChargeDisputeCreated, paymentIntent: "pi_foreign"), Is.True);
        Assert.That(await Service().StripeDisputeStatus("dp_1"), Is.Null, "no gateway configured");
        Assert.That(await db.FiniteTransactions.CountAsync(), Is.Zero);
    }

    /// <summary>Regression: the marker went through CreateTransaction and wrote the stale in-memory balance back</summary>
    [Test]
    public async Task LedgerMarkerLeavesTheBalanceOfTheUserAlone()
    {
        var stale = await users.GetOrCreate("buyer");
        await using (var other = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options))
        {
            (await other.Users.SingleAsync(u => u.ExternalId == "buyer")).Balance = 500;
            await other.SaveChangesAsync();
        }

        Assert.That(await transactions.AddLedgerMarker(stale, "marker-1"), Is.True);
        Assert.That(await transactions.AddLedgerMarker(stale, "marker-1"), Is.False);

        Assert.That(await db.Users.AsNoTracking().Select(u => u.Balance).SingleAsync(), Is.EqualTo(500));
        Assert.That(await db.FiniteTransactions.CountAsync(t => t.Reference == "marker-1"), Is.EqualTo(1));
    }
}
