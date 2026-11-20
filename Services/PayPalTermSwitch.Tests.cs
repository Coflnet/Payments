using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Models.PayPal;
using Coflnet.Payments.Services.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using PayPalCheckoutSdk.Core;

namespace Coflnet.Payments.Services;

/// <summary>
/// PayPal can not change a cycle's length: a switch to the year is a new PayPal subscription starting now. Nothing changes until the
/// payer approved it; on activation the old subscription is cancelled at PayPal and the new one's first sale extends the access the
/// old one paid for, so the paid days left are kept.
/// </summary>
public class PayPalTermSwitchTests : DirectSubscriptionTestBase
{
    private const long Year = 31536000;
    private FakePayPal paypal;
    private SubscriptionService service;
    private TopUpProduct year;
    private TopUpProduct quarter;
    private DateTime nextBilling;

    [SetUp]
    public async Task SetUpPaidFourWeeks()
    {
        paypal = new FakePayPal();
        nextBilling = new DateTime(DateTime.UtcNow.AddDays(20).Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        year = new TopUpProduct
        {
            Slug = "l_premium-year", Title = "52 weeks premium", Price = 99.69m, Cost = 23400, CurrencyCode = "eur",
            ProviderSlug = "lemonsqueezy", OwnershipSeconds = Year, Type = Product.ProductType.SERVICE
        };
        quarter = new TopUpProduct
        {
            Slug = "l_premium-quarter", Title = "13 weeks premium", Price = 26.69m, Cost = 6000, CurrencyCode = "eur",
            ProviderSlug = "lemonsqueezy", OwnershipSeconds = 7862400, Type = Product.ProductType.SERVICE
        };
        db.TopUpProducts.AddRange(year, quarter);
        db.PayPalPlans.Add(new PayPalPlan { ProductSlug = quarter.Slug, BillingWeeks = 13, RenewalWeeks = 4, Currency = "EUR", PlanId = "P-Q" });
        db.PayPalPlans.Add(new PayPalPlan { ProductSlug = "l_premium", BillingWeeks = 4, RenewalWeeks = 4, Currency = "EUR", PlanId = "P-4W" });
        db.PayPalPlans.Add(new PayPalPlan { ProductSlug = year.Slug, BillingWeeks = 52, RenewalWeeks = 4, Currency = "EUR", PlanId = "P-YEAR" });
        await db.SaveChangesAsync();
        await groups.AddProductToGroup(year, "premium");
        await groups.AddProductToGroup(quarter, "premium");
        var api = new PayPalSubscriptionApi(paypal);
        service = Service(paypal: api, paypalPlans: new PayPalPlanCatalog(db, api, new ConfigurationBuilder().Build()));
        var checkout = await SeedCheckout("paypal", "I-OLD", premium, 11.53m);
        (checkout.RecurringAmount, checkout.Country) = (11.53m, "DE");
        await db.SaveChangesAsync();
        SetSubscription("I-OLD", "ACTIVE", "P-4W", premium, nextBilling);
        Assert.That(await service.PayPalSaleCompleted(Sale("SALE-1", "I-OLD", "11.53")), Is.Not.Null);
    }

    private static string Time(DateTime time) => time.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private void SetSubscription(string id, string status, string planId, TopUpProduct plan, DateTime next)
    {
        paypal.Replies["/v1/billing/subscriptions/" + id] = JObject.Parse($$"""
            {"id":"{{id}}","status":"{{status}}","plan_id":"{{planId}}","custom_id":"{{SubscriptionService.PayPalCustomId(plan.Id, "buyer")}}",
             "create_time":"{{Time(DateTime.UtcNow)}}","billing_info":{"next_billing_time":"{{Time(next)}}"},
             "links":[{"href":"https://www.paypal.com/approve/{{id}}","rel":"approve","method":"GET"}]}
            """);
    }

    private static PayPalSale Sale(string id, string subscriptionId, string total) => JObject.Parse($$"""
        {"id":"{{id}}","state":"completed","amount":{"total":"{{total}}","currency":"EUR"},"billing_agreement_id":"{{subscriptionId}}",
         "create_time":"{{Time(DateTime.UtcNow)}}"}
        """).ToObject<PayPalSale>();

    private static SubscriptionTermSwitchRequest Request(int weeks = 52) => new()
    {
        Weeks = weeks, Declaration = StripeExtensionTests.Declaration("Switch to the year", "en"), ExpectedChargedNow = weeks == 13 ? 31.76m : 118.63m,
        ReturnUrl = "https://sky.coflnet.com/premium?switched", CancelUrl = "https://sky.coflnet.com/premium"
    };

    /// <summary>The payer's approval link for the new PayPal subscription <paramref name="id"/></summary>
    private Task<SubscriptionTermSwitchResult> RequestSwitch(string id = "I-NEW", int weeks = 52)
    {
        paypal.Replies["/v1/billing/subscriptions"] = JObject.Parse($$"""
            {"id":"{{id}}","status":"APPROVAL_PENDING","links":[{"href":"https://www.paypal.com/approve/{{id}}","rel":"approve","method":"GET"}]}
            """);
        var (planId, plan) = weeks == 13 ? ("P-Q", quarter) : ("P-YEAR", year);
        SetSubscription(id, "APPROVAL_PENDING", planId, plan, DateTime.UtcNow);
        return service.SwitchTerm("buyer", "I-OLD", Request(weeks));
    }

    private async Task Activate(string id = "I-NEW", string planId = "P-YEAR", TopUpProduct plan = null)
    {
        SetSubscription(id, "ACTIVE", planId, plan ?? year, DateTime.UtcNow.AddDays(364));
        paypal.Replies["/v1/billing/subscriptions/I-OLD/cancel"] = new JObject();
        Assert.That(await service.PayPalSubscriptionChanged(id), Is.Empty);
    }

    private Task<UserSubscription> Loaded(string id) =>
        db.Subscriptions.Include(s => s.Product).Include(s => s.RenewalProduct).SingleAsync(s => s.ExternalId == id);

    [Test]
    public async Task SwitchCreatesANewSubscriptionStartingNowAndChangesNothingBeforeTheApproval()
    {
        var expiry = (await Access("I-OLD")).Expires;

        var result = await RequestSwitch();

        Assert.That((result.Switched, result.Status, result.ApprovalUrl), Is.EqualTo((false, "approval_required", "https://www.paypal.com/approve/I-NEW")));
        Assert.That((result.ChargedNow, result.AccessUntil, result.RenewalPrice), Is.EqualTo((118.63m, expiry.AddSeconds(Year), 11.53m)));
        var body = paypal.Bodies["/v1/billing/subscriptions"];
        Assert.That(((string)body["plan_id"], body["start_time"]), Is.EqualTo(("P-YEAR", (JToken)null)), "starts now");
        var cycles = (JArray)body["plan"]!["billing_cycles"]!;
        Assert.That(cycles.Select(c => (string)c["pricing_scheme"]!["fixed_price"]!["value"]), Is.EqualTo(new[] { "118.63", "11.53" }));
        var request = await db.PaymentRequests.SingleAsync(r => r.SessionId == "I-NEW");
        Assert.That((request.ExtendsSubscriptionId, request.Country, request.Amount), Is.EqualTo(("I-OLD", "DE", 118.63m)));
        Assert.That((await db.ServicePerformanceDeclarations.SingleAsync()).PaymentRequestId, Is.EqualTo(request.Id));
        Assert.That((await Loaded("I-OLD")).Status, Is.EqualTo("active"));
        Assert.That(paypal.Bodies.ContainsKey("/v1/billing/subscriptions/I-OLD/cancel"), Is.False);
    }

    /// <summary>On activation the old subscription is cancelled; the new one's first sale adds the year on top of the old access</summary>
    [Test]
    public async Task ActivationReplacesTheOldSubscriptionAndTheFirstSaleExtendsItsAccess()
    {
        var expiry = (await Access("I-OLD")).Expires;
        await RequestSwitch();

        await Activate();

        Assert.That(paypal.Bodies.ContainsKey("/v1/billing/subscriptions/I-OLD/cancel"), Is.True);
        var old = await Loaded("I-OLD");
        Assert.That((old.Status, old.EndsAt), Is.EqualTo(("cancelled", (DateTime?)expiry)), "the paid days stay");
        var replacement = await Loaded("I-NEW");
        Assert.That((replacement.PreviousSubscriptionId, replacement.Product.Slug, replacement.RenewalProduct.Slug, replacement.RenewalPriceCents),
            Is.EqualTo(("I-OLD", "l_premium-year", "l_premium", (long?)1153)));

        var outcome = await service.PayPalSaleCompleted(Sale("SALE-N1", "I-NEW", "118.63"));
        await StripeExtensionTests.RecordPayment(Callback(service), outcome, "paypal");
        await StripeExtensionTests.RecordPayment(Callback(service), await service.PayPalSaleCompleted(Sale("SALE-N1", "I-NEW", "118.63")), "paypal");

        Assert.That(await Grants("paypal-sale-SALE-N1"), Is.EqualTo(1));
        Assert.That((await Access("I-NEW")).Expires, Is.EqualTo(expiry.AddSeconds(Year)));
        Assert.That(await Access("I-OLD"), Is.Null, "the access moved to the new subscription");
        var payment = publishedPayments.Single();
        Assert.That((payment.SubscriptionIsTermSwitch, payment.SubscriptionIsExtension, payment.SubscriptionIsRenewal, payment.SubscriptionPreviousBillingWeeks),
            Is.EqualTo(((bool?)true, (bool?)null, (bool?)false, (int?)4)));
        Assert.That((payment.DeclarationText, payment.ServiceEndsAtUtc), Is.EqualTo(("Switch to the year", (DateTime?)expiry.AddSeconds(Year))));
        Assert.That((await db.SubscriptionTermSwitches.SingleAsync()).Status, Is.EqualTo(TermSwitchStatus.Completed));
    }

    /// <summary>A full refund of the new subscription's first sale takes back the year it added, not the 4 weeks paid before</summary>
    [Test]
    public async Task RefundOfTheSwitchSaleRevertsOnlyTheYear()
    {
        var expiry = (await Access("I-OLD")).Expires;
        await RequestSwitch();
        await Activate();
        await service.PayPalSaleCompleted(Sale("SALE-N1", "I-NEW", "118.63"));
        paypal.Replies["/v1/payments/sale/SALE-N1"] = JObject.Parse("""
            {"id":"SALE-N1","state":"refunded","amount":{"total":"118.63","currency":"EUR"},"billing_agreement_id":"I-NEW","create_time":"2026-11-20T10:00:00Z"}
            """);

        await service.PayPalSaleRefunded("SALE-N1");

        Assert.That((await Access("I-NEW")).Expires, Is.EqualTo(expiry).Within(TimeSpan.FromSeconds(1)));
    }

    /// <summary>
    /// A chargeback of the switch sale takes back the year it added and cancels the new subscription; a won dispute grants the
    /// year again once
    /// </summary>
    [Test]
    public async Task DisputeOfTheSwitchSaleRevertsTheYearAndAWinRestoresIt()
    {
        var expiry = (await Access("I-OLD")).Expires;
        await RequestSwitch();
        await Activate();
        await service.PayPalSaleCompleted(Sale("SALE-N1", "I-NEW", "118.63"));
        paypal.Replies["/v1/payments/sale/SALE-N1"] = JObject.Parse("""
            {"id":"SALE-N1","state":"reversed","amount":{"total":"118.63","currency":"EUR"},"billing_agreement_id":"I-NEW","create_time":"2026-11-20T10:00:00Z"}
            """);
        paypal.Replies["/v1/billing/subscriptions/I-NEW/cancel"] = new JObject();

        Assert.That(await service.PayPalSaleDisputed("SALE-N1"), Is.True);
        await service.PayPalSaleDisputed("SALE-N1");

        Assert.That((await Access("I-NEW")).Expires, Is.EqualTo(expiry).Within(TimeSpan.FromSeconds(1)), "the 4 weeks paid before stay");
        Assert.That(paypal.Bodies.ContainsKey("/v1/billing/subscriptions/I-NEW/cancel"), Is.True);
        Assert.That((await Loaded("I-NEW")).Status, Is.EqualTo("expired"));
        Assert.That(await Grants("paypal-sale-SALE-1"), Is.EqualTo(1));

        Assert.That(await service.PayPalSaleDisputeWon("SALE-N1"), Is.True);
        await service.PayPalSaleDisputeWon("SALE-N1");

        Assert.That((await Access("I-NEW")).Expires, Is.EqualTo(expiry.AddSeconds(Year)).Within(TimeSpan.FromSeconds(1)));
        Assert.That(await Grants("paypal-sale-SALE-N1-disputewon"), Is.EqualTo(1));
    }

    /// <summary>A later refund of the old subscription's sale takes its 4 weeks from the access the new subscription took over</summary>
    [Test]
    public async Task RefundOfTheOldSaleAfterTheSwitchShortensTheAdoptedAccess()
    {
        var expiry = (await Access("I-OLD")).Expires;
        await RequestSwitch();
        await Activate();
        await service.PayPalSaleCompleted(Sale("SALE-N1", "I-NEW", "118.63"));
        paypal.Replies["/v1/payments/sale/SALE-1"] = JObject.Parse("""
            {"id":"SALE-1","state":"refunded","amount":{"total":"11.53","currency":"EUR"},"billing_agreement_id":"I-OLD","create_time":"2026-11-20T10:00:00Z"}
            """);

        await service.PayPalSaleRefunded("SALE-1");

        Assert.That((await Access("I-NEW")).Expires, Is.EqualTo(expiry.AddSeconds(Year - FourWeeks)).Within(TimeSpan.FromSeconds(1)));
    }

    /// <summary>A payer who never approves leaves the old subscription as it was; another term replaces the unapproved link</summary>
    [Test]
    public async Task AbandonedApprovalLeavesTheOldSubscriptionUntouched()
    {
        var expiry = (await Access("I-OLD")).Expires;
        await RequestSwitch();

        var again = await service.SwitchTerm("buyer", "I-OLD", Request());
        Assert.That(again.ApprovalUrl, Is.EqualTo("https://www.paypal.com/approve/I-NEW"), "the same link while it is pending");

        Assert.That(await service.ConvertDueRenewals(), Is.Zero);
        var old = await Loaded("I-OLD");
        Assert.That((old.Status, old.EndsAt, old.Product.Slug), Is.EqualTo(("active", (DateTime?)null, "l_premium")));
        Assert.That((await Access("I-OLD")).Expires, Is.EqualTo(expiry));
        Assert.That(paypal.Bodies.ContainsKey("/v1/billing/subscriptions/I-OLD/cancel"), Is.False);
        Assert.That(await db.Subscriptions.AnyAsync(s => s.ExternalId == "I-NEW"), Is.False);
    }

    /// <summary>The hourly run links a switch whose activation webhook was lost</summary>
    [Test]
    public async Task HourlyRunLinksAnApprovedSwitchWhoseWebhookWasLost()
    {
        await RequestSwitch();
        SetSubscription("I-NEW", "ACTIVE", "P-YEAR", year, DateTime.UtcNow.AddDays(364));
        paypal.Replies["/v1/billing/subscriptions/I-OLD/cancel"] = new JObject();

        await service.ConvertDueRenewals();

        Assert.That((await Loaded("I-NEW")).PreviousSubscriptionId, Is.EqualTo("I-OLD"));
        Assert.That((await Loaded("I-OLD")).Status, Is.EqualTo("cancelled"));
    }

    /// <summary>A second approved switch of the same subscription is refused: its sales are marked for a manual refund</summary>
    [Test]
    public async Task SecondApprovedSwitchIsRefused()
    {
        await RequestSwitch();
        var first = await db.SubscriptionTermSwitches.SingleAsync();
        first.ExpiresAt = DateTime.UtcNow.AddMinutes(-1); // no longer pending: a new request makes another one
        await db.SaveChangesAsync();
        await RequestSwitch("I-NEW2");
        await Activate();
        paypal.Replies["/v1/billing/subscriptions/I-NEW2/cancel"] = new JObject();

        await Activate("I-NEW2");
        Assert.That(await service.PayPalSaleCompleted(Sale("SALE-X", "I-NEW2", "118.63")), Is.Null);

        Assert.That((await Loaded("I-NEW2")).ContinuationRefusedAt, Is.Not.Null);
        Assert.That(await RefundMark("paypal", "SALE-X"), Is.EqualTo(PaymentRequest.Status.REFUND_PENDING));
        Assert.That(await Grants("paypal-sale-SALE-X"), Is.Zero);
    }

    [Test]
    public void SwitchNeedsTheReturnUrls()
    {
        var request = Request();
        request.CancelUrl = null;

        Assert.ThrowsAsync<ApiException>(() => service.SwitchTerm("buyer", "I-OLD", request));
    }

    /// <summary>
    /// A request for another term replaces the unapproved one (cancelled at PayPal); approving the replaced one later is refused and
    /// its sale marked for a manual refund
    /// </summary>
    [Test]
    public async Task ReplacedUnapprovedSwitchIsCancelledAndRefusedWhenApprovedLater()
    {
        await RequestSwitch("I-Q", 13);
        paypal.Replies["/v1/billing/subscriptions/I-Q/cancel"] = new JObject();

        await RequestSwitch();

        Assert.That(paypal.Bodies.ContainsKey("/v1/billing/subscriptions/I-Q/cancel"), Is.True);
        Assert.That((await db.SubscriptionTermSwitches.AsNoTracking().SingleAsync(r => r.NewSubscriptionId == "I-Q")).Status, Is.EqualTo(TermSwitchStatus.Failed));
        await Activate("I-Q", "P-Q", quarter);
        Assert.That(await service.PayPalSaleCompleted(Sale("SALE-Q", "I-Q", "31.76")), Is.Null);
        Assert.That((await Loaded("I-Q")).ContinuationRefusedAt, Is.Not.Null);
        Assert.That(await RefundMark("paypal", "SALE-Q"), Is.EqualTo(PaymentRequest.Status.REFUND_PENDING));
        Assert.That((await Loaded("I-OLD")).Status, Is.EqualTo("active"), "the refused switch does not touch the old subscription");
    }

    [Test]
    public async Task RequestedPayPalExtensionBlocksTheSwitch()
    {
        await ExtensionAtPayPal("APPROVAL_PENDING", nextBilling);

        Assert.That(Assert.ThrowsAsync<ApiException>(() => RequestSwitch()).Message, Is.EqualTo("extension_booked"));
    }

    /// <summary>
    /// An extension the payer gave up (cancelled, unknown to PayPal) or one made for another term end never blocks the switch, although
    /// its id stays recorded
    /// </summary>
    [TestCase("CANCELLED", 0)]
    [TestCase("EXPIRED", 0)]
    [TestCase("APPROVAL_PENDING", 60)]
    public async Task AbandonedOrStaleExtensionDoesNotBlockTheSwitch(string status, int daysAfterTheTermEnd)
    {
        await ExtensionAtPayPal(status, nextBilling.AddDays(daysAfterTheTermEnd));

        Assert.That((await RequestSwitch()).Status, Is.EqualTo("approval_required"));
    }

    private async Task ExtensionAtPayPal(string status, DateTime start)
    {
        var old = await Loaded("I-OLD");
        old.ExtensionSubscriptionId = "I-EXT";
        await db.SaveChangesAsync();
        paypal.Replies["/v1/billing/subscriptions/I-EXT"] = JObject.Parse($$"""
            {"id":"I-EXT","status":"{{status}}","plan_id":"P-YEAR","start_time":"{{Time(start)}}"}
            """);
    }

    /// <summary>
    /// A switch the hourly run expired (no approval in time; its promotion use given back) that the payer approves after all is
    /// completed and counts its promotion again; expiring again changes nothing
    /// </summary>
    [Test]
    public async Task LateApprovalOfAnExpiredSwitchIsAcceptedAndCountsThePromotionAgain()
    {
        db.DiscountCodes.Add(new DiscountCode
        {
            Code = "GTA6", AmountType = "percent", Amount = 25, PeriodAmounts = new() { [52] = 50 }, Duration = DiscountCode.FirstPaymentOnly,
            SubscriptionOnly = true, AutoApply = true, IsActive = true
        });
        await db.SaveChangesAsync();
        var request = Request();
        request.ExpectedChargedNow = 59.31m;
        paypal.Replies["/v1/billing/subscriptions"] = JObject.Parse("""
            {"id":"I-NEW","status":"APPROVAL_PENDING","links":[{"href":"https://www.paypal.com/approve/I-NEW","rel":"approve","method":"GET"}]}
            """);
        SetSubscription("I-NEW", "APPROVAL_PENDING", "P-YEAR", year, DateTime.UtcNow);
        await service.SwitchTerm("buyer", "I-OLD", request);
        await db.SubscriptionTermSwitches.ExecuteUpdateAsync(s => s.SetProperty(r => r.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        paypal.Replies["/v1/billing/subscriptions/I-NEW/cancel"] = new JObject();

        await service.ConvertDueRenewals();
        await service.ConvertDueRenewals();

        Assert.That(await Redeemed(), Is.Zero, "given back once");
        Assert.That((await db.SubscriptionTermSwitches.AsNoTracking().SingleAsync()).Status, Is.EqualTo(TermSwitchStatus.Expired));
        await Activate();
        Assert.That((await Loaded("I-NEW")).PreviousSubscriptionId, Is.EqualTo("I-OLD"));
        Assert.That((await db.SubscriptionTermSwitches.AsNoTracking().SingleAsync()).Status, Is.EqualTo(TermSwitchStatus.Completed));
        Assert.That(await Redeemed(), Is.EqualTo(1));
    }

    private async Task<int> Redeemed() => (await db.DiscountCodes.AsNoTracking().SingleAsync()).TimesRedeemed;

    /// <summary>An expired switch the payer approved meanwhile is left to its activation by the hourly run</summary>
    [Test]
    public async Task HourlyRunDoesNotExpireAnApprovedSwitch()
    {
        await RequestSwitch();
        await db.SubscriptionTermSwitches.ExecuteUpdateAsync(s => s.SetProperty(r => r.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        SetSubscription("I-NEW", "APPROVED", "P-YEAR", year, DateTime.UtcNow.AddDays(364));

        await service.ConvertDueRenewals();

        Assert.That((await db.SubscriptionTermSwitches.AsNoTracking().SingleAsync()).Status, Is.EqualTo(TermSwitchStatus.Pending));
    }

    /// <summary>One switch that can not be closed (PayPal unreadable) does not stop the run from closing the others</summary>
    [Test]
    public async Task HourlyRunContinuesAfterAFailingSwitch()
    {
        var past = DateTime.UtcNow.AddMinutes(-1);
        db.SubscriptionTermSwitches.AddRange(
            new SubscriptionTermSwitch { SubscriptionId = "I-OLD", Provider = "paypal", NewSubscriptionId = "I-UNREADABLE", Weeks = 52, Status = TermSwitchStatus.Pending, CreatedAt = past, ExpiresAt = past },
            new SubscriptionTermSwitch { SubscriptionId = "sub_other", Provider = "stripe", Weeks = 52, Status = TermSwitchStatus.Pending, CreatedAt = past, ExpiresAt = past });
        await db.SaveChangesAsync();

        await service.ConvertDueRenewals();

        var rows = await db.SubscriptionTermSwitches.AsNoTracking().OrderBy(r => r.Id).ToListAsync();
        Assert.That(rows.Select(r => r.Status), Is.EqualTo(new[] { TermSwitchStatus.Pending, TermSwitchStatus.Expired }));
    }

    /// <summary>A stale loaded row never overwrites a status another handler set meanwhile, nor gives its promotion use back twice</summary>
    [Test]
    public async Task StatusChangesOnlyApplyFromTheExpectedStatus()
    {
        var row = new SubscriptionTermSwitch { SubscriptionId = "I-OLD", Provider = "paypal", Weeks = 52, PromotionCode = "GTA6",
            Status = TermSwitchStatus.Pending, CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow };
        db.DiscountCodes.Add(new DiscountCode { Code = "GTA6", AmountType = "percent", Amount = 10, TimesRedeemed = 1 });
        db.SubscriptionTermSwitches.Add(row);
        await db.SaveChangesAsync();
        await db.SubscriptionTermSwitches.ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, TermSwitchStatus.Completed));
        var end = typeof(SubscriptionService).GetMethod("EndPendingTermSwitch", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        Assert.That(await (Task<bool>)end.Invoke(service, [row, TermSwitchStatus.Expired])!, Is.False);
        await db.SaveChangesAsync();

        Assert.That((await db.SubscriptionTermSwitches.AsNoTracking().SingleAsync()).Status, Is.EqualTo(TermSwitchStatus.Completed));
        Assert.That(await Redeemed(), Is.EqualTo(1));
    }

    /// <summary>An approval after the old subscription was cancelled is refused (its sale needs a manual refund)</summary>
    [Test]
    public async Task ApprovalAfterTheOldSubscriptionEndedIsRefused()
    {
        await RequestSwitch();
        var old = await Loaded("I-OLD");
        (old.Status, old.EndsAt) = ("cancelled", nextBilling);
        await db.SaveChangesAsync();

        await Activate();

        Assert.That((await Loaded("I-NEW")).ContinuationRefusedAt, Is.Not.Null);
        Assert.That(await service.PayPalSaleCompleted(Sale("SALE-N1", "I-NEW", "118.63")), Is.Null);
        Assert.That(await RefundMark("paypal", "SALE-N1"), Is.EqualTo(PaymentRequest.Status.REFUND_PENDING));
    }

    /// <summary>A sale of the old subscription after its access moved adds its 4 weeks on top of the new subscription's access, once</summary>
    [Test]
    public async Task LateSaleOfTheOldSubscriptionExtendsTheNewAccess()
    {
        var expiry = (await Access("I-OLD")).Expires;
        await RequestSwitch();
        await Activate();
        await service.PayPalSaleCompleted(Sale("SALE-N1", "I-NEW", "118.63"));
        SetSubscription("I-OLD", "CANCELLED", "P-4W", premium, nextBilling);

        await service.PayPalSaleCompleted(Sale("SALE-2", "I-OLD", "11.53"));
        await service.PayPalSaleCompleted(Sale("SALE-2", "I-OLD", "11.53"));

        Assert.That((await Access("I-NEW")).Expires, Is.EqualTo(expiry.AddSeconds(Year + FourWeeks)).Within(TimeSpan.FromSeconds(1)));
        Assert.That(await Access("I-OLD"), Is.Null);
        Assert.That(await Grants("paypal-sale-SALE-2"), Is.EqualTo(1));
    }

    /// <summary>The old subscription's cancellation failed at the activation: the hourly run retries it</summary>
    [Test]
    public async Task HourlyRunRetriesCancellingTheOldSubscription()
    {
        await RequestSwitch();
        SetSubscription("I-NEW", "ACTIVE", "P-YEAR", year, DateTime.UtcNow.AddDays(364));
        Assert.ThrowsAsync<HttpRequestException>(() => service.PayPalSubscriptionChanged("I-NEW"), "PayPal refused the cancellation");
        db.ChangeTracker.Clear();
        Assert.That((await Loaded("I-OLD")).Status, Is.EqualTo("active"));
        Assert.That((await Loaded("I-NEW")).PreviousSubscriptionId, Is.EqualTo("I-OLD"));

        paypal.Replies["/v1/billing/subscriptions/I-OLD/cancel"] = new JObject();
        await service.ConvertDueRenewals();

        db.ChangeTracker.Clear();
        Assert.That((await Loaded("I-OLD")).Status, Is.EqualTo("cancelled"));
    }

    /// <summary>The link was lost after the switch was marked completed (crash): the retry links it and cancels the old one</summary>
    [Test]
    public async Task CompletedSwitchWhoseLinkWasLostIsLinkedOnRetry()
    {
        await RequestSwitch();
        await db.SubscriptionTermSwitches.ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, TermSwitchStatus.Completed));

        await Activate();

        var replacement = await Loaded("I-NEW");
        Assert.That((replacement.PreviousSubscriptionId, replacement.ContinuationRefusedAt), Is.EqualTo(("I-OLD", (DateTime?)null)));
        Assert.That(paypal.Bodies.ContainsKey("/v1/billing/subscriptions/I-OLD/cancel"), Is.True);
        Assert.That(paypal.Bodies.ContainsKey("/v1/billing/subscriptions/I-NEW/cancel"), Is.False, "not refused");
        Assert.That(await service.PayPalSaleCompleted(Sale("SALE-N1", "I-NEW", "118.63")), Is.Not.Null);
        Assert.That(await RefundMark("paypal", "SALE-N1"), Is.Null);
    }

    /// <summary>The payer approves while the switch expires: it is not cancelled and its activation still links it</summary>
    [Test]
    public async Task ApprovalDuringTheSweepIsNotCancelled()
    {
        await RequestSwitch();
        await db.SubscriptionTermSwitches.ExecuteUpdateAsync(s => s.SetProperty(r => r.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        paypal.Replies["/v1/billing/subscriptions/I-NEW/cancel"] = new JObject();
        // still waiting when the run links and expires it, approved when it is about to cancel it
        var waiting = paypal.Replies["/v1/billing/subscriptions/I-NEW"];
        paypal.Sequences["/v1/billing/subscriptions/I-NEW"] = new Queue<JObject>([(JObject)waiting.DeepClone(), (JObject)waiting.DeepClone()]);
        SetSubscription("I-NEW", "APPROVED", "P-YEAR", year, DateTime.UtcNow.AddDays(364));

        await service.ConvertDueRenewals();

        Assert.That(paypal.Bodies.ContainsKey("/v1/billing/subscriptions/I-NEW/cancel"), Is.False);
        Assert.That((await db.SubscriptionTermSwitches.AsNoTracking().SingleAsync()).Status, Is.EqualTo(TermSwitchStatus.Expired));
        await Activate();
        Assert.That((await Loaded("I-NEW")).PreviousSubscriptionId, Is.EqualTo("I-OLD"));
        Assert.That((await db.SubscriptionTermSwitches.AsNoTracking().SingleAsync()).Status, Is.EqualTo(TermSwitchStatus.Completed));
    }

    private sealed class FakePayPal : PayPalHttpClient
    {
        public Dictionary<string, JObject> Replies { get; } = new();
        public Dictionary<string, JObject> Bodies { get; } = new();
        /// <summary>Replies given once each, in order, before <see cref="Replies"/></summary>
        public Dictionary<string, Queue<JObject>> Sequences { get; } = new();
        public FakePayPal() : base(new SandboxEnvironment("test", "test")) { }

        public override async Task<PayPalHttp.HttpResponse> Execute<T>(T request)
        {
            var path = request.Path.Split('?')[0];
            if (request.Content != null)
                Bodies[path] = JObject.Parse(await request.Content.ReadAsStringAsync());
            var reply = Sequences.TryGetValue(path, out var queue) && queue.Count > 0 ? queue.Dequeue() : null;
            if (reply == null && !Replies.TryGetValue(path, out reply))
                throw new HttpRequestException("Simulated PayPal API failure: " + request.Path);
            using var content = new StringContent(reply.ToString(), Encoding.UTF8, "application/json");
            var result = Encoder.DeserializeResponse(content, request.ResponseType);
            return new PayPalHttp.HttpResponse(content.Headers, HttpStatusCode.OK, result);
        }
    }
}
