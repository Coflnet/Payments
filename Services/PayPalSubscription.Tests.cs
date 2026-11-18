using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services.Subscriptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using PayPalCheckoutSdk.Core;
using Payments.Controllers;

namespace Coflnet.Payments.Services;

public class PayPalSubscriptionTests : DirectSubscriptionTestBase
{
    private const string VerifyPath = "/v1/notifications/verify-webhook-signature";
    private const string PlanId = "P-PREMIUM";
    private FakePayPal paypal;
    private SubscriptionService service;

    [SetUp]
    public void SetUpPayPal()
    {
        paypal = new FakePayPal();
        paypal.Replies[VerifyPath] = JObject.Parse("""{"verification_status":"SUCCESS"}""");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["PAYPAL:SUBSCRIPTION_PLANS:l_premium"] = PlanId
        }).Build();
        var api = new PayPalSubscriptionApi(paypal);
        service = Service(paypal: api, paypalPlans: new PayPalPlanCatalog(db, api, config));
    }

    private void SetSubscription(string status = "ACTIVE", string planId = PlanId, string customId = null, bool paid = true)
    {
        customId ??= SubscriptionService.PayPalCustomId(premium.Id, "buyer");
        var lastPayment = paid ? ""","last_payment":{"amount":{"currency_code":"EUR","value":"9.69"},"time":"2026-11-17T10:00:00Z"}""" : "";
        paypal.Replies["/v1/billing/subscriptions/I-SUB"] = JObject.Parse($$"""
            {"id":"I-SUB","status":"{{status}}","plan_id":"{{planId}}","custom_id":"{{customId}}","create_time":"2026-11-17T09:59:00Z",
             "billing_info":{"next_billing_time":"2026-12-15T10:00:00Z"{{lastPayment}}},
             "links":[{"href":"https://www.paypal.com/webapps/billing/subscriptions?ba_token=BA-1","rel":"approve","method":"GET"}]}
            """);
    }

    private void SetSale(string id = "SALE-1", string state = "completed", string total = "9.69")
    {
        paypal.Replies["/v1/payments/sale/" + id] = JObject.Parse($$"""
            {"id":"{{id}}","state":"{{state}}","amount":{"total":"{{total}}","currency":"EUR"},
             "billing_agreement_id":"I-SUB","create_time":"2026-11-17T10:00:00Z"}
            """);
    }

    private void SetTransactions(params string[] saleIds)
    {
        var transactions = new JArray(saleIds.Select(id => JObject.Parse($$"""
            {"id":"{{id}}","status":"COMPLETED","time":"2026-11-17T10:00:00Z",
             "amount_with_breakdown":{"gross_amount":{"currency_code":"EUR","value":"9.69"} } }
            """)));
        paypal.Replies["/v1/billing/subscriptions/I-SUB/transactions"] = new JObject { ["transactions"] = transactions };
    }

    private static string SaleCompleted(string id = "SALE-1", string total = "9.69") => $$"""
        {"id":"WH-{{id}}","event_type":"PAYMENT.SALE.COMPLETED","resource":{"id":"{{id}}","state":"completed",
         "amount":{"total":"{{total}}","currency":"EUR"},"billing_agreement_id":"I-SUB","create_time":"2026-11-17T10:00:00Z"} }
        """;

    private const string Activated = """
        {"id":"WH-ACT","event_type":"BILLING.SUBSCRIPTION.ACTIVATED","resource":{"id":"I-SUB","status":"ACTIVE","plan_id":"P-PREMIUM"}}
        """;

    private async Task<IActionResult> Post(string json)
    {
        var controller = Callback(service, new Dictionary<string, string> { ["PAYPAL:WEBHOOK_ID"] = "hook" }, paypal);
        controller.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        foreach (var header in new[] { "AUTH-ALGO", "CERT-URL", "TRANSMISSION-ID", "TRANSMISSION-SIG", "TRANSMISSION-TIME" })
            controller.Request.Headers["PAYPAL-" + header] = "test-" + header;
        return await controller.Paypal();
    }

    /// <summary>
    /// The agreed price is the gross incl. VAT; the record splits the VAT of the buyer's country off
    /// (9.69 net + 19% = 11.53 gross, VAT 1.84) and the creator share is based on the net part.
    /// </summary>
    [Test]
    public async Task GrossSaleIsRecordedWithItsVatSplitOff()
    {
        var request = await SeedCheckout("paypal", "I-SUB", premium, 11.53m);
        request.User.Country = "DE";
        await db.SaveChangesAsync();
        SetSubscription();
        SetSale(total: "11.53");

        Assert.That(await Post(SaleCompleted(total: "11.53")), Is.TypeOf<OkResult>());

        Assert.That((await Subscription("I-SUB")).PriceCents, Is.EqualTo(1153));
        var record = await db.PaymentRecords.SingleAsync();
        Assert.That((record.GrossAmount, record.TaxAmount, record.Subtotal, record.NetAmount), Is.EqualTo((11.53m, 1.84m, 9.69m, 9.69m)));
        Assert.That(record.TaxRate, Is.EqualTo(19.0));
        Assert.That(publishedPayments.Single().PayedAmount, Is.EqualTo(11.53), "the event carries what the buyer paid");
    }

    /// <summary>
    /// Regression: the VAT of every sale was split with the user's current country, which later purchases overwrite.
    /// A German subscription (11.53 incl. 19%) renewed after the user bought from Austria was recorded with 20% VAT
    /// (1.92, net 9.61) under AT; from a country without a rate (CH) with no VAT at all and the whole 11.53 as creator base.
    /// </summary>
    [TestCase("AT")]
    [TestCase("CH")]
    public async Task VatOfASaleStaysWithTheCheckoutCountryAfterTheUserMoved(string laterCountry) =>
        await AssertGermanSaleRecorded(laterCountry);

    /// <summary>
    /// Regression: the PayPal subscription never took over the checkout's creator code, so no creator share was
    /// recorded for PayPal subscriptions at all (and the payment record named no creator code).
    /// </summary>
    [Test]
    public async Task CreatorCodeOfTheCheckoutEarnsItsShareOfTheNetPrice()
    {
        await AssertGermanSaleRecorded("DE");
        var subscription = await Subscription("I-SUB");
        Assert.That(subscription.CreatorCode, Is.EqualTo("CREATOR"));
        Assert.That((await db.PaymentRecords.SingleAsync()).CreatorCode, Is.EqualTo("CREATOR"));
        Assert.That((await db.CreatorCodeRevenues.SingleAsync()).CreatorRevenue, Is.EqualTo(0.969m));
    }

    private async Task AssertGermanSaleRecorded(string laterCountry)
    {
        db.CreatorCodes.Add(new CreatorCode { Code = "CREATOR", CreatorUserId = "creator", DiscountPercent = 0,
            RevenueSharePercent = 10, IsActive = true });
        var request = await SeedCheckout("paypal", "I-SUB", premium, 11.53m);
        request.Country = "DE";
        request.CreatorCode = "CREATOR";
        request.User.Country = laterCountry;
        await db.SaveChangesAsync();
        SetSubscription();
        SetSale(total: "11.53");

        Assert.That(await Post(SaleCompleted(total: "11.53")), Is.TypeOf<OkResult>());

        var record = await db.PaymentRecords.SingleAsync();
        Assert.That((record.Country, record.TaxAmount, record.NetAmount, record.TaxRate), Is.EqualTo(("DE", 1.84m, 9.69m, 19.0)));
        Assert.That((await db.CreatorCodeRevenues.SingleAsync()).FinalPrice, Is.EqualTo(9.69m));
    }

    [Test]
    public async Task SaleCompletedGrantsOnceAndActivationSafetyNetCannotGrantItAgain()
    {
        await SeedCheckout("paypal", "I-SUB", premium, 9.69m);
        SetSubscription();
        SetSale();
        SetTransactions("SALE-1");

        Assert.That(await Post(SaleCompleted()), Is.TypeOf<OkResult>());
        Assert.That(await Post(SaleCompleted()), Is.TypeOf<OkResult>());
        Assert.That(await Post(Activated), Is.TypeOf<OkResult>());

        Assert.That(await Grants("paypal-sale-SALE-1"), Is.EqualTo(1));
        Assert.That(publishedPayments, Has.Count.EqualTo(1));
        Assert.That(await PaymentRecords("paypal"), Is.EqualTo(1));
        var subscription = await Subscription("I-SUB");
        Assert.That(subscription.ProviderSlug, Is.EqualTo("paypal"));
        Assert.That(subscription.Status, Is.EqualTo("active"));
        Assert.That(subscription.PriceCents, Is.EqualTo(969));
        Assert.That(subscription.ProviderPlanId, Is.EqualTo(PlanId));
        Assert.That((await Access("I-SUB")).Product.Slug, Is.EqualTo("l_premium"));
    }

    [Test]
    public async Task ActivationGrantsTheListedFirstPaymentUnderItsSaleId()
    {
        await SeedCheckout("paypal", "I-SUB", premium, 9.69m);
        SetSubscription();
        SetSale();
        SetTransactions("SALE-1");

        Assert.That(await Post(Activated), Is.TypeOf<OkResult>());
        Assert.That(await Grants("paypal-sale-SALE-1"), Is.EqualTo(1));
        // The SALE.COMPLETED of the same payment arrives afterwards.
        Assert.That(await Post(SaleCompleted()), Is.TypeOf<OkResult>());
        Assert.That(await Grants("paypal-sale-SALE-1"), Is.EqualTo(1));
        Assert.That(publishedPayments, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ActivationSkipsTransactionsThatAreNotProvablyTheSale()
    {
        await SeedCheckout("paypal", "I-SUB", premium, 9.69m);
        SetSubscription();
        SetTransactions("TXN-OTHER-ID");
        Assert.That(await Post(Activated), Is.TypeOf<OkResult>());
        Assert.That(await db.FiniteTransactions.CountAsync(), Is.Zero);
        Assert.That((await Subscription("I-SUB")).Status, Is.EqualTo("active"));
    }

    [TestCase("0.01")]
    [TestCase("19.38")]
    public async Task SaleMustChargeThePriceThisServiceSet(string total)
    {
        await SeedCheckout("paypal", "I-SUB", premium, 9.69m);
        SetSubscription();
        Assert.That(await Post(SaleCompleted(total: total)), Is.TypeOf<OkResult>());
        Assert.That(await db.FiniteTransactions.CountAsync(), Is.Zero);
        Assert.That(await RefundMark("paypal", "SALE-1"), Is.EqualTo(PaymentRequest.Status.REFUND_PENDING));
    }

    [Test]
    public async Task DiscountedCheckoutPriceIsTheAgreedPrice()
    {
        await SeedCheckout("paypal", "I-SUB", premium, 8.72m);
        SetSubscription();
        Assert.That(await Post(SaleCompleted(total: "9.69")), Is.TypeOf<OkResult>());
        Assert.That(await db.FiniteTransactions.CountAsync(), Is.Zero);
        Assert.That(await Post(SaleCompleted("SALE-2", "8.72")), Is.TypeOf<OkResult>());
        Assert.That(await Grants("paypal-sale-SALE-2"), Is.EqualTo(1));
    }

    [Test]
    public async Task SubscriptionNotCreatedByCheckoutIsIgnored()
    {
        SetSubscription();
        Assert.That(await Post(Activated), Is.TypeOf<OkResult>());
        Assert.That(await Post(SaleCompleted()), Is.TypeOf<OkResult>());
        Assert.That(await db.Subscriptions.CountAsync(), Is.Zero);
        Assert.That(await db.FiniteTransactions.CountAsync(), Is.Zero);
        Assert.That(await RefundMark("paypal", "SALE-1"), Is.EqualTo(PaymentRequest.Status.REFUND_PENDING),
            "money taken for a subscription we did not sell is refunded, not dropped");
    }

    [TestCase("P-OTHER", null)]
    [TestCase(PlanId, "1;someone-else")]
    public async Task ActivatedSubscriptionMustMatchThePlanAndCustomId(string planId, string customId)
    {
        await SeedCheckout("paypal", "I-SUB", premium, 9.69m);
        SetSubscription(planId: planId, customId: customId);
        SetSale();
        SetTransactions("SALE-1");
        Assert.That(await Post(Activated), Is.TypeOf<OkResult>());
        Assert.That(await db.Subscriptions.CountAsync(), Is.Zero);
        Assert.That(await db.FiniteTransactions.CountAsync(), Is.Zero);
    }

    /// <summary>
    /// Regression: a retried SALE.COMPLETED found the sale granted and returned nothing, so a payment whose
    /// recording failed (e.g. publishing to Kafka) was never recorded.
    /// </summary>
    [Test]
    public async Task SaleIsRecordedWhenTheWebhookIsRetriedAfterAPublishFailure()
    {
        await SeedCheckout("paypal", "I-SUB", premium, 9.69m);
        SetSubscription();
        failingPublishes = 1;
        Assert.That(await Post(SaleCompleted()), Is.InstanceOf<StatusCodeResult>().With.Property("StatusCode").EqualTo(400));
        Assert.That(await PaymentRecords("paypal"), Is.Zero);
        Assert.That(await Post(SaleCompleted()), Is.TypeOf<OkResult>());
        Assert.That(await Post(SaleCompleted()), Is.TypeOf<OkResult>());
        Assert.That(await Grants("paypal-sale-SALE-1"), Is.EqualTo(1));
        Assert.That(await PaymentRecords("paypal"), Is.EqualTo(1));
        Assert.That(publishedPayments, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ApprovalPendingSubscriptionIsNotFulfilled()
    {
        await SeedCheckout("paypal", "I-SUB", premium, 9.69m);
        SetSubscription("APPROVAL_PENDING", paid: false);
        Assert.That(await Post(Activated), Is.TypeOf<OkResult>());
        Assert.That(await db.Subscriptions.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task SaleOfASubscriptionStillAwaitingApprovalIsRetried()
    {
        await SeedCheckout("paypal", "I-SUB", premium, 9.69m);
        SetSubscription("APPROVED", paid: false);
        Assert.That(await Post(SaleCompleted()), Is.InstanceOf<StatusCodeResult>().With.Property("StatusCode").EqualTo(400));
        SetSubscription();
        Assert.That(await Post(SaleCompleted()), Is.TypeOf<OkResult>());
        Assert.That(await Grants("paypal-sale-SALE-1"), Is.EqualTo(1));
    }

    [Test]
    public async Task SaleCompletedAfterAQuickCancellationIsStillGranted()
    {
        await SeedCheckout("paypal", "I-SUB", premium, 9.69m);
        SetSubscription("CANCELLED");
        Assert.That(await Post(SaleCompleted()), Is.TypeOf<OkResult>());
        Assert.That(await Grants("paypal-sale-SALE-1"), Is.EqualTo(1));
        var subscription = await Subscription("I-SUB");
        Assert.That(subscription.Status, Is.EqualTo("cancelled"));
        Assert.That(subscription.EndsAt, Is.EqualTo((await Access("I-SUB")).Expires));
    }

    [Test]
    public async Task RefundedSaleRevokesItsPeriod()
    {
        await SeedCheckout("paypal", "I-SUB", premium, 9.69m);
        SetSubscription();
        await Post(SaleCompleted());
        SetSale(state: "refunded");
        var refund = """
            {"id":"WH-REF","event_type":"PAYMENT.SALE.REFUNDED","resource":{"id":"REFUND-1","state":"completed",
             "sale_id":"SALE-1","amount":{"total":"9.69","currency":"EUR"}}}
            """;
        Assert.That(await Post(refund), Is.TypeOf<OkResult>());
        Assert.That(await Post(refund), Is.TypeOf<OkResult>());
        Assert.That(await db.RefundedSubscriptionInvoices.CountAsync(i => i.InvoiceId == "SALE-1"), Is.EqualTo(1));
        Assert.That((await Access("I-SUB")).Expires, Is.LessThan(DateTime.UtcNow.AddMinutes(1)));
        Assert.That((await db.PaymentRecords.SingleAsync()).Status, Is.EqualTo(PaymentRecordStatus.Refunded));
    }

    private async Task ReverseScenario(string refundState, string refundAmount)
    {
        db.CreatorCodes.Add(new CreatorCode { Code = "CREATOR", CreatorUserId = "creator", DiscountPercent = 0,
            RevenueSharePercent = 10, IsActive = true });
        var request = await SeedCheckout("paypal", "I-SUB", premium, 9.69m);
        request.CreatorCode = "CREATOR";
        await db.SaveChangesAsync();
        SetSubscription();
        await Post(SaleCompleted());
        SetSale(state: refundState);
        var refund = """
            {"id":"WH-REF","event_type":"PAYMENT.SALE.REFUNDED","resource":{"id":"REFUND-1","state":"completed",
             "sale_id":"SALE-1","amount":{"total":"AMOUNT","currency":"EUR"}}}
            """.Replace("AMOUNT", refundAmount);
        await Post(refund);
        await Post(refund);
    }

    /// <summary>
    /// Regression: PayPal refunds never took back the creator share booked with the sale.
    /// </summary>
    [Test]
    public async Task FullRefundReversesTheCreatorShareOnce()
    {
        await ReverseScenario("refunded", "9.69");

        Assert.That(await db.CreatorCodeRevenues.Where(r => r.CreatorRevenue < 0).Select(r => r.CreatorRevenue).ToListAsync(),
            Is.EqualTo(new[] { -0.969m }));
        Assert.That(await db.CreatorCodeRevenues.SumAsync(r => r.CreatorRevenue), Is.Zero);
    }

    /// <summary>
    /// Regression (negative case): PayPal's refund resource may carry the amount negative, which reversed nothing.
    /// </summary>
    [TestCase("4.845")]
    [TestCase("-4.845")]
    public async Task PartialRefundReversesItsProportionOfTheShareAndKeepsAccess(string refundAmount)
    {
        await ReverseScenario("partially_refunded", refundAmount);

        Assert.That(await db.CreatorCodeRevenues.Where(r => r.CreatorRevenue < 0).Select(r => r.CreatorRevenue).ToListAsync(),
            Is.EqualTo(new[] { -0.4845m }));
        Assert.That((await Access("I-SUB")).Expires, Is.GreaterThan(DateTime.UtcNow.AddDays(20)));
    }

    /// <summary>
    /// A payer in another country than the checkout is recorded (and logged), the sale is still granted.
    /// </summary>
    [Test]
    public async Task PayerCountryAndCheckoutIpAreRecordedAndADifferingPayerIsNotRejected()
    {
        var request = await SeedCheckout("paypal", "I-SUB", premium, 11.53m);
        request.Country = "DE";
        request.CheckoutRegion = "BY";
        request.CreateOnIp = IPAddress.Parse("203.0.113.7");
        await db.SaveChangesAsync();
        SetSubscription();
        paypal.Replies["/v1/billing/subscriptions/I-SUB"]["subscriber"] =
            JObject.Parse("""{"shipping_address":{"address":{"country_code":"at"}}}""");
        SetSale(total: "11.53");

        await Post(SaleCompleted(total: "11.53"));

        var record = await db.PaymentRecords.SingleAsync();
        Assert.That((await Subscription("I-SUB")).CheckoutCountry, Is.EqualTo("DE"), "the US direct cap counts by it");
        Assert.That((await Subscription("I-SUB")).CheckoutRegion, Is.EqualTo("BY"), "and the per-state cap by this");
        Assert.That(record.CheckoutRegion, Is.EqualTo("BY"));
        Assert.That((record.CheckoutCountry, record.BillingCountry, record.PaymentMethodCountry, record.IpAddress),
            Is.EqualTo(("DE", "AT", (string)null, "203.0.113.7")));
        Assert.That(await Grants("paypal-sale-SALE-1"), Is.EqualTo(1));
    }

    /// <summary>
    /// With NO_SHIPPING PayPal returns no shipping address; a card funded subscription still carries the card's
    /// billing country, a wallet funded one has no country evidence at all.
    /// </summary>
    [TestCase("""{"payment_source":{"card":{"billing_address":{"country_code":"fr"}}}}""", "FR")]
    [TestCase("""{"payer_id":"PAYER","email_address":"a@b.c"}""", null)]
    public async Task PayerCountryComesFromTheCardBillingAddressOrStaysEmpty(string subscriber, string expected)
    {
        var request = await SeedCheckout("paypal", "I-SUB", premium, 11.53m);
        request.Country = "DE";
        await db.SaveChangesAsync();
        SetSubscription();
        paypal.Replies["/v1/billing/subscriptions/I-SUB"]["subscriber"] = JObject.Parse(subscriber);
        SetSale(total: "11.53");

        await Post(SaleCompleted(total: "11.53"));

        Assert.That((await db.PaymentRecords.SingleAsync()).BillingCountry, Is.EqualTo(expected));
    }

    [Test]
    public async Task CancelIsImmediateAtPayPalButAccessRunsToThePaidEndAndCannotResume()
    {
        await SeedCheckout("paypal", "I-SUB", premium, 9.69m);
        SetSubscription();
        await Post(SaleCompleted());
        paypal.Replies["/v1/billing/subscriptions/I-SUB/cancel"] = new JObject();

        await service.CancelSubscription("buyer", "I-SUB");

        var cancel = paypal.Bodies["/v1/billing/subscriptions/I-SUB/cancel"];
        Assert.That(cancel["reason"]?.ToString(), Is.Not.Empty);
        var subscription = await Subscription("I-SUB");
        Assert.That(subscription.Status, Is.EqualTo("cancelled"));
        Assert.That(subscription.EndsAt, Is.EqualTo((await Access("I-SUB")).Expires));
        var listed = (await service.GetUserSubscriptions("buyer")).Single();
        Assert.That(listed.CanResume, Is.False);
        Assert.That(listed.CanChangePlan, Is.False);
        Assert.ThrowsAsync<ApiException>(() => service.ResumeSubscription("buyer", "I-SUB"));
        // The CANCELLED webhook that follows keeps the paid end.
        SetSubscription("CANCELLED");
        await Post("""{"id":"WH-C","event_type":"BILLING.SUBSCRIPTION.CANCELLED","resource":{"id":"I-SUB","status":"CANCELLED"}}""");
        Assert.That(subscription.EndsAt, Is.EqualTo((await Access("I-SUB")).Expires));
    }

    [Test]
    public void CreateBodyOverridesTheFirstAndRegularCyclePriceAndKeepsCustomIdShort()
    {
        var body = PayPalSubscriptionApi.CreateBody(new PayPalSubscriptionRequest(PlanId,
            SubscriptionService.PayPalCustomId(12, "user-id"), 8.72m, "EUR", "https://ok", "https://cancel", "a@b.c"));
        Assert.That(body["plan_id"]!.ToString(), Is.EqualTo(PlanId));
        Assert.That(body["custom_id"]!.ToString(), Has.Length.LessThanOrEqualTo(127));
        var cycles = (JArray)body["plan"]!["billing_cycles"]!;
        Assert.That(cycles.Select(c => c["sequence"]!.Value<int>()), Is.EqualTo(new[] { 1, 2 }));
        Assert.That(cycles.Select(c => c["pricing_scheme"]!["fixed_price"]!["value"]!.ToString()), Is.EqualTo(new[] { "8.72", "8.72" }));
        Assert.That(body["application_context"]!["return_url"]!.ToString(), Is.EqualTo("https://ok"));
    }

    [Test]
    public async Task FirstPaymentOnlyPriceAppliesToTheFirstSaleThenRenewalsPayTheRecurringPrice()
    {
        var request = await SeedCheckout("paypal", "I-SUB", premium, 4.84m);
        request.RecurringAmount = 9.69m;
        await db.SaveChangesAsync();
        SetSubscription();

        Assert.That(await Post(SaleCompleted("SALE-1", "4.84")), Is.TypeOf<OkResult>());
        Assert.That(await Post(SaleCompleted("SALE-2", "9.69")), Is.TypeOf<OkResult>());
        Assert.That(await Post(SaleCompleted("SALE-3", "4.84")), Is.TypeOf<OkResult>());

        Assert.That(await Grants("paypal-sale-SALE-1"), Is.EqualTo(1));
        Assert.That(await Grants("paypal-sale-SALE-2"), Is.EqualTo(1));
        Assert.That(await Grants("paypal-sale-SALE-3"), Is.Zero, "a renewal at the discounted price is not what was agreed");
        Assert.That((await Subscription("I-SUB")).PriceCents, Is.EqualTo(969));
    }

    private (PayPalPlanCatalog catalog, PayPalSubscriptionApi api) CatalogWithoutConfig()
    {
        paypal.Replies["/v1/catalogs/products"] = JObject.Parse("""{"id":"PROD-NEW"}""");
        paypal.Replies["/v1/billing/plans"] = JObject.Parse("""{"id":"P-NEW"}""");
        var api = new PayPalSubscriptionApi(paypal);
        return (new PayPalPlanCatalog(db, api, new ConfigurationBuilder().Build()), api);
    }

    [Test]
    public async Task PlanIsCreatedOnFirstUseAsTrialThenRegularAndReused()
    {
        var (catalog, _) = CatalogWithoutConfig();

        Assert.That(await catalog.GetOrCreate(premium), Is.EqualTo("P-NEW"));
        paypal.Replies["/v1/billing/plans"] = JObject.Parse("""{"id":"P-OTHER"}""");
        Assert.That(await catalog.GetOrCreate(premium), Is.EqualTo("P-NEW"), "stored, not created again");

        var row = await db.PayPalPlans.SingleAsync();
        Assert.That((row.ProductSlug, row.BillingWeeks, row.Currency, row.PayPalProductId), Is.EqualTo(("l_premium", 4, "EUR", "PROD-NEW")));
        var product = paypal.Bodies["/v1/catalogs/products"];
        Assert.That((string)product["type"], Is.EqualTo("SERVICE"));
        Assert.That((string)product["category"], Is.EqualTo("SOFTWARE"));
        var cycles = (JArray)paypal.Bodies["/v1/billing/plans"]["billing_cycles"]!;
        Assert.That(cycles.Select(c => (string)c["tenure_type"]), Is.EqualTo(new[] { "TRIAL", "REGULAR" }));
        Assert.That(cycles.Select(c => (int)c["sequence"]), Is.EqualTo(new[] { 1, 2 }));
        Assert.That(cycles.Select(c => (int)c["total_cycles"]), Is.EqualTo(new[] { 1, 0 }));
        Assert.That(cycles.Select(c => (string)c["frequency"]!["interval_unit"]), Is.All.EqualTo("WEEK"));
        Assert.That(cycles.Select(c => (int)c["frequency"]!["interval_count"]), Is.All.EqualTo(4));
        Assert.That(await catalog.IsPlanOf("P-NEW", premium), Is.True);
        Assert.That(await catalog.IsPlanOf("P-FOREIGN", premium), Is.False);
    }

    /// <summary>
    /// Regression guard for concurrent first checkouts: the unique key keeps one plan, the loser uses the stored one.
    /// </summary>
    [Test]
    public async Task ConcurrentFirstCheckoutUsesThePlanThatWasStoredFirst()
    {
        var api = new Mock<IPayPalSubscriptionApi>();
        api.Setup(a => a.CreateCatalogProduct(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync("PROD-1");
        api.Setup(a => a.CreatePlan(It.IsAny<PayPalPlanRequest>())).Returns(async () =>
        {
            await using var other = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
            other.PayPalPlans.Add(new PayPalPlan { ProductSlug = "l_premium", BillingWeeks = 4, RenewalWeeks = 4, Currency = "EUR", PlanId = "P-WINNER" });
            await other.SaveChangesAsync();
            return "P-LOSER";
        });
        var catalog = new PayPalPlanCatalog(db, api.Object, new ConfigurationBuilder().Build());

        Assert.That(await catalog.GetOrCreate(premium), Is.EqualTo("P-WINNER"));
        Assert.That(await db.PayPalPlans.CountAsync(), Is.EqualTo(1));
    }

    /// <summary>
    /// Owner decision (§ 309 Nr. 9 b BGB): a 52 week plan is one 52 week TRIAL cycle, then 4-week REGULAR cycles at the
    /// 4-week list price of the tier; each PayPal billing cycle has its own frequency.
    /// </summary>
    [Test]
    public async Task YearlyPlanIsAYearOfTrialThenFourWeekRegularCycles()
    {
        var (catalog, _) = CatalogWithoutConfig();
        await catalog.GetOrCreate(Year(), premium);
        var cycles = (JArray)paypal.Bodies["/v1/billing/plans"]["billing_cycles"]!;
        Assert.That(cycles.Select(c => (string)c["tenure_type"]), Is.EqualTo(new[] { "TRIAL", "REGULAR" }));
        Assert.That(cycles.Select(c => (int)c["frequency"]!["interval_count"]!), Is.EqualTo(new[] { 52, 4 }));
        Assert.That(cycles.Select(c => (string)c["pricing_scheme"]!["fixed_price"]!["value"]!), Is.EqualTo(new[] { "99.69", "9.69" }));
        var row = await db.PayPalPlans.SingleAsync();
        Assert.That((row.BillingWeeks, row.RenewalWeeks), Is.EqualTo((52, 4)));
    }

    [Test]
    public void YearlyPlanWithoutAFourWeekPlanIsNotCreated()
    {
        var (catalog, _) = CatalogWithoutConfig();
        Assert.ThrowsAsync<ApiException>(() => catalog.GetOrCreate(Year()));
    }

    /// <summary>
    /// Regression: a configured PAYPAL:SUBSCRIPTION_PLANS override was assumed to continue every 4 weeks, also for 13/52 week
    /// plans, where nothing proves it (one repeating its period would renew unlawfully). It only applies to 4-week plans now.
    /// </summary>
    [Test]
    public async Task ConfiguredPlanIsIgnoredForAYear()
    {
        CatalogWithoutConfig();
        var year = Year();
        var configured = new PayPalPlanCatalog(db, new PayPalSubscriptionApi(paypal), new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["PAYPAL:SUBSCRIPTION_PLANS:l_premium_52"] = "P-CONFIGURED-YEAR", ["PAYPAL:SUBSCRIPTION_PLANS:l_premium"] = "P-CONFIGURED"
            }).Build());

        Assert.That(await configured.GetOrCreate(year, premium), Is.EqualTo("P-NEW"));
        Assert.That(await configured.IsPlanOf("P-CONFIGURED-YEAR", year), Is.False);
        Assert.That(await configured.RenewalWeeks("P-CONFIGURED-YEAR", year), Is.Null);
        Assert.That(await configured.GetOrCreate(premium), Is.EqualTo("P-CONFIGURED"), "4-week plans still use the override");
    }

    /// <summary>
    /// A 52 week plan made before the 4-week renewal (it repeats every 52 weeks) is not reused for new checkouts, but
    /// subscriptions on it are still accepted and keep renewing for their period.
    /// </summary>
    [Test]
    public async Task LegacyYearlyPlanIsReplacedButStillAccepted()
    {
        var (catalog, _) = CatalogWithoutConfig();
        var year = Year();
        db.PayPalPlans.Add(new PayPalPlan { ProductSlug = year.Slug, BillingWeeks = 52, RenewalWeeks = 52, Currency = "EUR", PlanId = "P-LEGACY" });
        await db.SaveChangesAsync();

        Assert.That(await catalog.GetOrCreate(year, premium), Is.EqualTo("P-NEW"));
        Assert.That(await catalog.IsPlanOf("P-LEGACY", year), Is.True);
        Assert.That(await catalog.RenewalWeeks("P-LEGACY", year), Is.EqualTo(52));
        Assert.That(await catalog.RenewalWeeks("P-NEW", year), Is.EqualTo(4));
    }

    private static TopUpProduct Year() => new()
    {
        Slug = "l_premium_52", Title = "year", Price = 99.69m, Cost = 23400, CurrencyCode = "eur",
        ProviderSlug = "lemonsqueezy", OwnershipSeconds = 31536000, Type = Product.ProductType.SERVICE
    };

    [Test]
    public async Task ConfiguredPlanIdOverridesTheCreatedPlan()
    {
        var (catalog, _) = CatalogWithoutConfig();
        var withConfig = new PayPalPlanCatalog(db, new PayPalSubscriptionApi(paypal), new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string> { ["PAYPAL:SUBSCRIPTION_PLANS:l_premium"] = "P-CONFIGURED" }).Build());
        Assert.That(await withConfig.GetOrCreate(premium), Is.EqualTo("P-CONFIGURED"));
        Assert.That(await db.PayPalPlans.CountAsync(), Is.Zero);
        Assert.That(await catalog.IsPlanOf("P-CONFIGURED", premium), Is.False);
    }

    [Test]
    public async Task SubscriptionOfACreatedPlanIsAccepted()
    {
        var (catalog, api) = CatalogWithoutConfig();
        await catalog.GetOrCreate(premium);
        service = Service(paypal: api, paypalPlans: catalog);
        await SeedCheckout("paypal", "I-SUB", premium, 9.69m);
        SetSubscription(planId: "P-NEW");
        Assert.That(await Post(SaleCompleted()), Is.TypeOf<OkResult>());
        Assert.That(await Grants("paypal-sale-SALE-1"), Is.EqualTo(1));
    }

    /// <summary>
    /// Regression: a PayPal subscription priced with a promotion code could be approved (and kept the promotion price
    /// forever) long after the code expired, because nothing limited how late a pending checkout was activated.
    /// </summary>
    [Test]
    public async Task SubscriptionApprovedAfterItsCodeExpiredIsCancelledAndNotGranted()
    {
        var request = await SeedCheckout("paypal", "I-SUB", premium, 9.69m);
        request.ExpiresAt = new DateTime(2026, 11, 16, 9, 59, 0, DateTimeKind.Utc); // first payment 24h 1min later
        await db.SaveChangesAsync();
        SetSubscription();
        paypal.Replies["/v1/billing/subscriptions/I-SUB/cancel"] = new JObject();

        Assert.That(await Post(SaleCompleted()), Is.TypeOf<OkResult>());

        Assert.That(await Grants("paypal-sale-SALE-1"), Is.Zero);
        Assert.That(paypal.Bodies.ContainsKey("/v1/billing/subscriptions/I-SUB/cancel"), Is.True);
        Assert.That(await db.Subscriptions.AnyAsync(s => s.ExternalId == "I-SUB"), Is.False);
        Assert.That((await db.PaymentRequests.SingleAsync(r => r.SessionId == "SALE-1")).State, Is.EqualTo(PaymentRequest.Status.REFUND_PENDING));
        Assert.That((await db.PaymentRequests.SingleAsync(r => r.SessionId == "I-SUB")).State, Is.EqualTo(PaymentRequest.Status.REJECTED));
    }

    [Test]
    public async Task SubscriptionApprovedWithinTheGraceAfterItsCodeExpiredIsGranted()
    {
        var request = await SeedCheckout("paypal", "I-SUB", premium, 9.69m);
        request.ExpiresAt = new DateTime(2026, 11, 16, 10, 1, 0, DateTimeKind.Utc); // first payment 23h 59min later
        await db.SaveChangesAsync();
        SetSubscription();

        Assert.That(await Post(SaleCompleted()), Is.TypeOf<OkResult>());

        Assert.That(await Grants("paypal-sale-SALE-1"), Is.EqualTo(1));
        Assert.That(paypal.Bodies.ContainsKey("/v1/billing/subscriptions/I-SUB/cancel"), Is.False);
    }

    /// <summary>A 52 week plan in the premium tier group, sold on a PayPal plan of the given REGULAR cycle length</summary>
    private async Task<TopUpProduct> YearOnPlan(string planId, int renewalWeeks)
    {
        var year = Year();
        db.TopUpProducts.Add(year);
        db.PayPalPlans.Add(new PayPalPlan { ProductSlug = year.Slug, BillingWeeks = 52, RenewalWeeks = renewalWeeks, Currency = "EUR", PlanId = planId });
        await db.SaveChangesAsync();
        await groups.AddProductToGroup(year, "premium");
        var (catalog, api) = CatalogWithoutConfig();
        service = Service(paypal: api, paypalPlans: catalog);
        var request = await SeedCheckout("paypal", "I-SUB", year, 118.63m);
        request.RecurringAmount = renewalWeeks == 4 ? 11.53m : null;
        request.Country = "DE";
        await db.SaveChangesAsync();
        SetSubscription(planId: planId, customId: SubscriptionService.PayPalCustomId(year.Id, "buyer"));
        return year;
    }

    private Task<UserSubscription> Loaded() =>
        db.Subscriptions.Include(s => s.Product).Include(s => s.RenewalProduct).SingleAsync(s => s.ExternalId == "I-SUB");

    /// <summary>
    /// Owner decision (§ 309 Nr. 9 b BGB): the year is the TRIAL cycle, every following sale a 4-week REGULAR cycle at the
    /// 4-week price, which converts the subscription to the 4-week plan and grants 4 weeks.
    /// </summary>
    [Test]
    public async Task YearContinuesAsTheFourWeekPlanFromTheSecondSale()
    {
        await YearOnPlan("P-YEAR", 4);

        Assert.That(await Post(SaleCompleted("SALE-1", "118.63")), Is.TypeOf<OkResult>());
        var subscription = await Loaded();
        Assert.That(subscription.Product.Slug, Is.EqualTo("l_premium_52"));
        Assert.That((subscription.RenewalProduct.Slug, subscription.RenewalPriceCents), Is.EqualTo(("l_premium", 1153L)));
        Assert.That((await Access("I-SUB")).Expires, Is.EqualTo(DateTime.UtcNow.AddSeconds(31536000)).Within(TimeSpan.FromMinutes(1)));

        Assert.That(await Post(SaleCompleted("SALE-2", "11.53")), Is.TypeOf<OkResult>());
        Assert.That(await Post(SaleCompleted("SALE-3", "118.63")), Is.TypeOf<OkResult>());

        subscription = await Loaded();
        Assert.That((subscription.Product.Slug, subscription.PriceCents), Is.EqualTo(("l_premium", 1153L)));
        Assert.That(await Grants("paypal-sale-SALE-2"), Is.EqualTo(1));
        Assert.That(await Grants("paypal-sale-SALE-3"), Is.Zero, "a renewal at the year's price is not what was agreed");
        Assert.That((await RefundMark("paypal", "SALE-3")).ToString(), Is.EqualTo(nameof(PaymentRequest.Status.REFUND_PENDING)));
        var access = await Access("I-SUB");
        Assert.That(access.Product.Slug, Is.EqualTo("l_premium"));
        Assert.That(access.Expires, Is.EqualTo(DateTime.UtcNow.AddSeconds(31536000 + FourWeeks)).Within(TimeSpan.FromMinutes(1)));
        // The purchase confirmation email (SkyEventBroker) reads the terms from the events
        Assert.That(publishedPayments.Select(p => (p.SubscriptionBillingWeeks, p.SubscriptionRenewalWeeks, p.SubscriptionRenewalAmount, p.SubscriptionIsRenewal)),
            Is.EqualTo(new (int?, int?, decimal?, bool?)[] { (52, 4, 11.53m, false), (4, 4, 11.53m, true) }));
        var renewal = await db.PaymentRecords.SingleAsync(r => r.ExternalOrderId == "SALE-2");
        Assert.That((renewal.GrossAmount, renewal.TaxAmount), Is.EqualTo((11.53m, 1.84m)));
        Assert.That((renewal.ProductId, renewal.CoinAmount), Is.EqualTo((premium.Id, (long)premium.Cost)), "recorded as the 4-week plan");
    }

    /// <summary>
    /// A PayPal plan made before the 4-week renewal can not be changed without the payer: those subscriptions renew for the
    /// year as before (and are reported by the renewal conversion run)
    /// </summary>
    [Test]
    public async Task YearOnALegacyPlanRenewsForTheYearAsBefore()
    {
        await YearOnPlan("P-LEGACY", 52);

        Assert.That(await Post(SaleCompleted("SALE-1", "118.63")), Is.TypeOf<OkResult>());
        Assert.That(await Post(SaleCompleted("SALE-2", "118.63")), Is.TypeOf<OkResult>());

        var subscription = await Loaded();
        Assert.That(subscription.RenewalProduct, Is.Null);
        Assert.That(subscription.Product.Slug, Is.EqualTo("l_premium_52"));
        Assert.That(await Grants("paypal-sale-SALE-2"), Is.EqualTo(1));
        Assert.That((await Access("I-SUB")).Expires, Is.EqualTo(DateTime.UtcNow.AddSeconds(2 * 31536000)).Within(TimeSpan.FromMinutes(1)));
        Assert.That(publishedPayments.Select(p => (p.SubscriptionBillingWeeks, p.SubscriptionRenewalWeeks, p.SubscriptionRenewalAmount, p.SubscriptionIsRenewal)),
            Is.EqualTo(new (int?, int?, decimal?, bool?)[] { (52, 52, 118.63m, false), (52, 52, 118.63m, true) }), "the legacy plan repeats its year");
    }

    private sealed class FakePayPal : PayPalHttpClient
    {
        public Dictionary<string, JObject> Replies { get; } = new();
        public Dictionary<string, JObject> Bodies { get; } = new();
        public FakePayPal() : base(new SandboxEnvironment("test", "test")) { }

        public override async Task<PayPalHttp.HttpResponse> Execute<T>(T request)
        {
            var path = request.Path.Split('?')[0];
            if (request.Content != null)
                Bodies[path] = JObject.Parse(await request.Content.ReadAsStringAsync());
            if (!Replies.TryGetValue(path, out var reply))
                throw new HttpRequestException("Simulated PayPal API failure: " + request.Path);
            using var content = new StringContent(reply.ToString(), Encoding.UTF8, "application/json");
            var result = Encoder.DeserializeResponse(content, request.ResponseType);
            return new PayPalHttp.HttpResponse(content.Headers, HttpStatusCode.OK, result);
        }
    }
}
