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
            other.PayPalPlans.Add(new PayPalPlan { ProductSlug = "l_premium", BillingWeeks = 4, Currency = "EUR", PlanId = "P-WINNER" });
            await other.SaveChangesAsync();
            return "P-LOSER";
        });
        var catalog = new PayPalPlanCatalog(db, api.Object, new ConfigurationBuilder().Build());

        Assert.That(await catalog.GetOrCreate(premium), Is.EqualTo("P-WINNER"));
        Assert.That(await db.PayPalPlans.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task EachBillingPeriodGetsItsOwnPlanOfThatLength()
    {
        var (catalog, _) = CatalogWithoutConfig();
        var year = new TopUpProduct { Slug = "l_premium_52", Title = "year", Price = 99.69m, Cost = 23400, CurrencyCode = "eur",
            ProviderSlug = "lemonsqueezy", OwnershipSeconds = 31536000, Type = Product.ProductType.SERVICE };
        await catalog.GetOrCreate(year);
        var cycle = paypal.Bodies["/v1/billing/plans"]["billing_cycles"]![1]!;
        Assert.That((int)cycle["frequency"]!["interval_count"]!, Is.EqualTo(52));
        Assert.That((await db.PayPalPlans.SingleAsync()).BillingWeeks, Is.EqualTo(52));
    }

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
        request.ExpiresAt = new DateTime(2026, 11, 17, 9, 0, 0, DateTimeKind.Utc); // first payment at 10:00
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
        request.ExpiresAt = new DateTime(2026, 11, 17, 9, 45, 0, DateTimeKind.Utc); // first payment 15 minutes later
        await db.SaveChangesAsync();
        SetSubscription();

        Assert.That(await Post(SaleCompleted()), Is.TypeOf<OkResult>());

        Assert.That(await Grants("paypal-sale-SALE-1"), Is.EqualTo(1));
        Assert.That(paypal.Bodies.ContainsKey("/v1/billing/subscriptions/I-SUB/cancel"), Is.False);
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
