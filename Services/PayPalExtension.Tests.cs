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
/// Owner decision: a PayPal year can be extended by another year before it ends. PayPal can not change the next cycle without
/// the payer, so the extension is a new PayPal subscription on the same plan starting when the year ends; once approved it
/// replaces the current one, which is cancelled at PayPal while its paid year keeps running.
/// </summary>
public class PayPalExtensionTests : DirectSubscriptionTestBase
{
    private const long Year = 31536000;
    private FakePayPal paypal;
    private SubscriptionService service;
    private TopUpProduct year;
    private DateTime termEnd;

    [SetUp]
    public async Task SetUpPaidYear()
    {
        EnableExtensions = true;
        paypal = new FakePayPal();
        termEnd = new DateTime(DateTime.UtcNow.AddDays(30).Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        year = new TopUpProduct
        {
            Slug = "l_premium_52", Title = "52 weeks premium", Price = 99.69m, Cost = 23400, CurrencyCode = "eur",
            ProviderSlug = "lemonsqueezy", OwnershipSeconds = Year, Type = Product.ProductType.SERVICE
        };
        db.TopUpProducts.Add(year);
        db.PayPalPlans.Add(new PayPalPlan { ProductSlug = year.Slug, BillingWeeks = 52, RenewalWeeks = 4, Currency = "EUR", PlanId = "P-YEAR" });
        await db.SaveChangesAsync();
        await groups.AddProductToGroup(year, "premium");
        var api = new PayPalSubscriptionApi(paypal);
        service = Service(paypal: api, paypalPlans: new PayPalPlanCatalog(db, api, new ConfigurationBuilder().Build()));
        var checkout = await SeedCheckout("paypal", "I-OLD", year, 118.63m);
        checkout.RecurringAmount = 11.53m;
        checkout.Country = "DE";
        await db.SaveChangesAsync();
        SetSubscription("I-OLD", "ACTIVE", termEnd);
        Assert.That(await service.PayPalSaleCompleted(Sale("SALE-1", "I-OLD", "118.63", DateTime.UtcNow)), Is.Not.Null);
    }

    private static string Time(DateTime time) => time.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private void SetSubscription(string id, string status, DateTime nextBilling)
    {
        paypal.Replies["/v1/billing/subscriptions/" + id] = JObject.Parse($$"""
            {"id":"{{id}}","status":"{{status}}","plan_id":"P-YEAR","custom_id":"{{SubscriptionService.PayPalCustomId(year.Id, "buyer")}}",
             "create_time":"2026-11-17T09:59:00Z","billing_info":{"next_billing_time":"{{Time(nextBilling)}}"},
             "links":[{"href":"https://www.paypal.com/approve/{{id}}","rel":"approve","method":"GET"}]}
            """);
    }

    private static PayPalSale Sale(string id, string subscriptionId, string total, DateTime time) => JObject.Parse($$"""
        {"id":"{{id}}","state":"completed","amount":{"total":"{{total}}","currency":"EUR"},"billing_agreement_id":"{{subscriptionId}}",
         "create_time":"{{Time(time)}}"}
        """).ToObject<PayPalSale>();

    private SubscriptionExtensionRequest Extension(string text = "One more year") => new()
    {
        Declaration = StripeExtensionTests.Declaration(text, "en"), ReturnUrl = "https://sky.coflnet.com/premium?extended",
        CancelUrl = "https://sky.coflnet.com/premium"
    };

    /// <summary>The payer's approval link for a new PayPal subscription I-NEW</summary>
    private async Task<SubscriptionExtensionResult> RequestExtension(string text = "One more year")
    {
        paypal.Replies["/v1/billing/subscriptions"] = JObject.Parse("""
            {"id":"I-NEW","status":"APPROVAL_PENDING","links":[{"href":"https://www.paypal.com/approve/I-NEW","rel":"approve","method":"GET"}]}
            """);
        SetSubscription("I-NEW", "APPROVAL_PENDING", termEnd);
        return await service.ExtendLongTerm("buyer", "I-OLD", Extension(text));
    }

    /// <summary>The payer approved I-NEW: PayPal activates it (start_time in the future)</summary>
    private async Task Activate()
    {
        SetSubscription("I-NEW", "ACTIVE", termEnd);
        paypal.Replies["/v1/billing/subscriptions/I-OLD/cancel"] = new JObject();
        paypal.Replies["/v1/billing/subscriptions/I-NEW/cancel"] = new JObject();
        Assert.That(await service.PayPalSubscriptionChanged("I-NEW"), Is.Empty);
    }

    private Task<UserSubscription> Loaded(string id) =>
        db.Subscriptions.Include(s => s.Product).Include(s => s.RenewalProduct).SingleAsync(s => s.ExternalId == id);

    [Test]
    public async Task ExtensionIsANewSubscriptionStartingWhenTheYearEnds()
    {
        var result = await RequestExtension();

        Assert.That((result.Extended, result.ApprovalUrl, result.TermEndsAt), Is.EqualTo((false, "https://www.paypal.com/approve/I-NEW", termEnd)));
        Assert.That(result.ExtendedUntil, Is.EqualTo(termEnd.AddDays(364)));
        var body = paypal.Bodies["/v1/billing/subscriptions"];
        Assert.That((string)body["plan_id"], Is.EqualTo("P-YEAR"));
        Assert.That(((DateTime)body["start_time"]).ToUniversalTime(), Is.EqualTo(termEnd));
        Assert.That((string)body["custom_id"], Is.EqualTo(SubscriptionService.PayPalCustomId(year.Id, "buyer")));
        var cycles = (JArray)body["plan"]!["billing_cycles"]!;
        Assert.That(cycles.Select(c => (string)c["pricing_scheme"]!["fixed_price"]!["value"]), Is.EqualTo(new[] { "118.63", "11.53" }),
            "TRIAL: the year at 99.69 + 19% VAT, REGULAR: 4 weeks at 9.69 + 19%");
        var old = await Loaded("I-OLD");
        Assert.That((old.ExtensionSubscriptionId, old.ExtensionStartsAt, old.Status), Is.EqualTo(("I-NEW", (DateTime?)null, "active")),
            "nothing changes until the payer approved it");
        var request = await db.PaymentRequests.SingleAsync(r => r.SessionId == "I-NEW");
        Assert.That((request.Amount, request.RecurringAmount, request.Country), Is.EqualTo((118.63m, (decimal?)11.53m, "DE")));
        Assert.That((await db.ServicePerformanceDeclarations.SingleAsync()).PaymentRequestId, Is.EqualTo(request.Id));
    }

    /// <summary>A second request before the approval returns the same link; after the approval it is refused</summary>
    [Test]
    public async Task OnlyOneExtensionCanBeApproved()
    {
        await RequestExtension();
        paypal.Bodies.Clear();

        var again = await service.ExtendLongTerm("buyer", "I-OLD", Extension());

        Assert.That(again.ApprovalUrl, Is.EqualTo("https://www.paypal.com/approve/I-NEW"));
        Assert.That(paypal.Bodies.ContainsKey("/v1/billing/subscriptions"), Is.False, "no second PayPal subscription");
        SetSubscription("I-NEW", "ACTIVE", termEnd);
        Assert.ThrowsAsync<ApiException>(() => service.ExtendLongTerm("buyer", "I-OLD", Extension()));
    }

    /// <summary>
    /// On activation the new subscription is linked as the continuation and the old one is cancelled at PayPal; the paid year
    /// keeps its access
    /// </summary>
    [Test]
    public async Task ActivationCancelsTheOldSubscriptionAndKeepsItsPaidYear()
    {
        var expiry = (await Access("I-OLD")).Expires;
        await RequestExtension();

        await Activate();

        Assert.That(paypal.Bodies.ContainsKey("/v1/billing/subscriptions/I-OLD/cancel"), Is.True);
        var old = await Loaded("I-OLD");
        Assert.That((old.Status, old.ExtensionStartsAt, old.ExtensionPriceCents), Is.EqualTo(("cancelled", (DateTime?)termEnd, (long?)11863)));
        Assert.That((await Access("I-OLD")).Expires, Is.EqualTo(expiry), "cancelling does not revoke the paid year");
        var continuation = await Loaded("I-NEW");
        Assert.That((continuation.PreviousSubscriptionId, continuation.Status, continuation.PriceCents), Is.EqualTo(("I-OLD", "active", (long?)11863)));
        Assert.That((continuation.Product.Slug, continuation.RenewalProduct.Slug, continuation.RenewalPriceCents),
            Is.EqualTo(("l_premium_52", "l_premium", (long?)1153)), "itself a year continuing every 4 weeks");
        Assert.That((await db.PaymentRequests.SingleAsync(r => r.SessionId == "I-NEW")).State, Is.EqualTo(PaymentRequest.Status.PAID));

        await service.PayPalSubscriptionChanged("I-NEW");
        Assert.That(await db.Subscriptions.CountAsync(), Is.EqualTo(2), "a redelivery links nothing twice");
    }

    /// <summary>
    /// The first sale of the continuation pays the extension year (granted as a year, not converted) and its confirmation carries
    /// the extension's declaration; the next sale converts it to the 4-week plan as usual
    /// </summary>
    [Test]
    public async Task FirstSaleOfTheContinuationIsTheExtensionYear()
    {
        await RequestExtension("Extend by a year");
        await Activate();
        SetSubscription("I-NEW", "ACTIVE", termEnd.AddDays(364));

        var outcome = await service.PayPalSaleCompleted(Sale("SALE-N1", "I-NEW", "118.63", termEnd));
        await StripeExtensionTests.RecordPayment(Callback(service), outcome, "paypal");

        Assert.That(await Grants("paypal-sale-SALE-N1"), Is.EqualTo(1));
        Assert.That((await Loaded("I-NEW")).Product.Slug, Is.EqualTo("l_premium_52"));
        var payment = publishedPayments.Single();
        Assert.That((payment.SubscriptionIsExtension, payment.SubscriptionIsRenewal, payment.DeclarationText, payment.LegalLocale),
            Is.EqualTo(((bool?)true, (bool?)true, "Extend by a year", "en")));
        Assert.That((payment.ServiceStartsAtUtc, payment.ServiceEndsAtUtc), Is.EqualTo(((DateTime?)termEnd, (DateTime?)termEnd.AddDays(364))));
        Assert.That(payment.SubscriptionExternalId, Is.EqualTo("I-NEW"));

        Assert.That(await service.PayPalSaleCompleted(Sale("SALE-N2", "I-NEW", "11.53", termEnd.AddDays(364))), Is.Not.Null);
        Assert.That((await Loaded("I-NEW")).Product.Slug, Is.EqualTo("l_premium"));
    }

    /// <summary>The subscription's own first sale carries the checkout's declaration (Task 1 for PayPal)</summary>
    [Test]
    public async Task FirstSaleCarriesTheCheckoutDeclaration()
    {
        var request = await db.PaymentRequests.SingleAsync(r => r.SessionId == "I-OLD");
        db.ServicePerformanceDeclarations.Add(StripeExtensionTests.Evidence(request.Id, "Start now", "de"));
        await db.SaveChangesAsync();

        var outcome = await service.PayPalSaleCompleted(Sale("SALE-1", "I-OLD", "118.63", DateTime.UtcNow));
        await StripeExtensionTests.RecordPayment(Callback(service), outcome, "paypal");

        var payment = publishedPayments.Single();
        Assert.That((payment.DeclarationText, payment.LegalLocale, payment.SubscriptionIsRenewal, payment.SubscriptionIsExtension),
            Is.EqualTo(("Start now", "de", (bool?)false, (bool?)null)));
        Assert.That(payment.ServiceEndsAtUtc, Is.EqualTo(termEnd));
    }

    /// <summary>A slot bundle keeps its slots (and their assignments): the continuation's first sale extends them</summary>
    [Test]
    public async Task ContinuationAdoptsTheSlotsOfTheOldSubscription()
    {
        var buyer = await Buyer();
        var old = await Loaded("I-OLD");
        var slot = new TierSlot { UserId = buyer.Id, SubscriptionId = "I-OLD", Tier = "premium", Expires = termEnd };
        db.TierSlots.Add(slot);
        var continuation = new UserSubscription
        {
            User = buyer, Product = old.Product, ProviderSlug = "paypal", ExternalId = "I-SLOTS", PreviousSubscriptionId = "I-OLD",
            Status = "active", RenewsAt = termEnd
        };
        db.Subscriptions.Add(continuation);
        await db.SaveChangesAsync();
        old.Product.SlotCount = 1;
        var adopt = typeof(SubscriptionService).GetMethod("AdoptPreviousSlots", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        await (Task)adopt.Invoke(service, [continuation])!;

        db.ChangeTracker.Clear();
        Assert.That((await db.TierSlots.SingleAsync()).SubscriptionId, Is.EqualTo("I-SLOTS"));
    }

    [Test]
    public async Task CancellingTheOldSubscriptionCancelsTheApprovedExtension()
    {
        await RequestExtension();
        await Activate();
        paypal.Bodies.Clear();

        await service.CancelSubscription("buyer", "I-OLD");

        Assert.That(paypal.Bodies.ContainsKey("/v1/billing/subscriptions/I-NEW/cancel"), Is.True);
        Assert.That(paypal.Bodies.ContainsKey("/v1/billing/subscriptions/I-OLD/cancel"), Is.False, "cancelled already when it was continued");
        Assert.That((await Loaded("I-NEW")).Status, Is.EqualTo("cancelled"));
        Assert.That((await Loaded("I-OLD")).ExtensionStartsAt, Is.Null);
    }

    /// <summary>
    /// Regression (review): a refused continuation was only marked cancelled, and its sale was granted when it came after the
    /// activation. The refusal is persisted and every sale of it goes to a manual refund, in either webhook order.
    /// </summary>
    [Test]
    public async Task ExtensionApprovedAfterTheCancellationIsRefusedAndNeverGranted()
    {
        await RequestExtension();
        paypal.Replies["/v1/billing/subscriptions/I-OLD/cancel"] = new JObject();
        await service.CancelSubscription("buyer", "I-OLD");

        await Activate();

        Assert.That(paypal.Bodies.ContainsKey("/v1/billing/subscriptions/I-NEW/cancel"), Is.True);
        var continuation = await Loaded("I-NEW");
        Assert.That((continuation.Status, continuation.PreviousSubscriptionId), Is.EqualTo(("cancelled", (string)null)));
        Assert.That(continuation.ContinuationRefusedAt, Is.Not.Null);
        Assert.That((await Loaded("I-OLD")).ExtensionStartsAt, Is.Null);
        await AssertSaleIsNotGranted();
    }

    [Test]
    public async Task RefusedExtensionWhoseSaleArrivesBeforeTheActivationIsNeverGranted()
    {
        await RequestExtension();
        paypal.Replies["/v1/billing/subscriptions/I-OLD/cancel"] = new JObject();
        await service.CancelSubscription("buyer", "I-OLD");
        SetSubscription("I-NEW", "ACTIVE", termEnd);
        paypal.Replies["/v1/billing/subscriptions/I-NEW/cancel"] = new JObject();

        await AssertSaleIsNotGranted();
        Assert.That(await service.PayPalSubscriptionChanged("I-NEW"), Is.Empty, "the activation after it grants nothing either");
        Assert.That((await Loaded("I-NEW")).ContinuationRefusedAt, Is.Not.Null);
        Assert.That(await Grants("paypal-sale-SALE-N1"), Is.Zero);
    }

    private async Task AssertSaleIsNotGranted()
    {
        Assert.That(await service.PayPalSaleCompleted(Sale("SALE-N1", "I-NEW", "118.63", DateTime.UtcNow)), Is.Null);
        Assert.That(await Grants("paypal-sale-SALE-N1"), Is.Zero);
        Assert.That((await RefundMark("paypal", "SALE-N1")).ToString(), Is.EqualTo(nameof(PaymentRequest.Status.REFUND_PENDING)));
    }

    /// <summary>An approval that is only activated within the last day of the term is refused like a late booking</summary>
    [Test]
    public async Task ExtensionActivatedWithinTheLastDayIsRefused()
    {
        await RequestExtension();
        var old = await Loaded("I-OLD");
        old.RenewsAt = DateTime.UtcNow.AddHours(12);
        await db.SaveChangesAsync();

        await Activate();

        Assert.That((await Loaded("I-NEW")).ContinuationRefusedAt, Is.Not.Null);
        Assert.That((await Loaded("I-OLD")).Status, Is.EqualTo("active"), "the old subscription keeps running");
    }

    /// <summary>Regression (review): the old subscription still showed the extension after the payer cancelled it in PayPal</summary>
    [Test]
    public async Task ExtensionCancelledInPayPalIsNotShownAnymore()
    {
        await RequestExtension();
        await Activate();
        var listed = (await service.GetUserSubscriptions("buyer")).Single(s => s.ExternalId == "I-OLD");
        Assert.That(listed.ExtendedUntil, Is.EqualTo(termEnd.AddDays(364)));

        SetSubscription("I-NEW", "CANCELLED", termEnd);
        await service.PayPalSubscriptionChanged("I-NEW");

        db.ChangeTracker.Clear();
        Assert.That((await service.GetUserSubscriptions("buyer")).Single(s => s.ExternalId == "I-OLD").ExtendedUntil, Is.Null);
    }

    /// <summary>Regression (review): the term end came from a possibly stale RenewsAt instead of PayPal</summary>
    [Test]
    public async Task ExtensionStartsAtTheTermEndPayPalReports()
    {
        var old = await Loaded("I-OLD");
        old.RenewsAt = termEnd.AddDays(-3); // a missed webhook
        await db.SaveChangesAsync();

        var result = await RequestExtension();

        Assert.That(result.TermEndsAt, Is.EqualTo(termEnd));
        Assert.That(((DateTime)paypal.Bodies["/v1/billing/subscriptions"]["start_time"]).ToUniversalTime(), Is.EqualTo(termEnd));
    }

    /// <summary>The hourly run links an approved extension whose activation webhook was lost and cancels the old subscription</summary>
    [Test]
    public async Task HourlyRunLinksAnApprovedExtensionWithoutItsWebhook()
    {
        await RequestExtension();
        SetSubscription("I-NEW", "ACTIVE", termEnd);
        paypal.Replies["/v1/billing/subscriptions/I-OLD/cancel"] = new JObject();

        await service.ConvertDueRenewals();

        Assert.That(paypal.Bodies.ContainsKey("/v1/billing/subscriptions/I-OLD/cancel"), Is.True);
        Assert.That((await Loaded("I-NEW")).PreviousSubscriptionId, Is.EqualTo("I-OLD"));
        Assert.That((await Loaded("I-OLD")).Status, Is.EqualTo("cancelled"));
        paypal.Bodies.Clear();
        await service.ConvertDueRenewals();
        Assert.That(paypal.Bodies, Is.Empty, "linked: not checked again");
    }

    /// <summary>
    /// Regression (review): a linked extension whose cancellation of the old subscription failed was no longer checked by the
    /// hourly run, so the old subscription kept billing next to it
    /// </summary>
    [Test]
    public async Task HourlyRunRetriesTheCancellationOfTheOldSubscription()
    {
        await RequestExtension();
        SetSubscription("I-NEW", "ACTIVE", termEnd);

        await service.ConvertDueRenewals(); // PayPal refuses the cancellation (no reply)

        Assert.That((await Loaded("I-NEW")).PreviousSubscriptionId, Is.EqualTo("I-OLD"));
        Assert.That((await Loaded("I-OLD")).Status, Is.EqualTo("active"));
        paypal.Replies["/v1/billing/subscriptions/I-OLD/cancel"] = new JObject();
        paypal.Bodies.Clear();
        db.ChangeTracker.Clear();

        await service.ConvertDueRenewals();

        Assert.That(paypal.Bodies.ContainsKey("/v1/billing/subscriptions/I-OLD/cancel"), Is.True);
        db.ChangeTracker.Clear();
        Assert.That((await Loaded("I-OLD")).Status, Is.EqualTo("cancelled"));
    }

    /// <summary>
    /// A refused extension whose cancellation PayPal refused is cancelled by the hourly run (it is never linked, and not reported
    /// as an unlinked activation every hour)
    /// </summary>
    [Test]
    public async Task HourlyRunRetriesTheCancellationOfARefusedExtension()
    {
        await RequestExtension();
        var old = await Loaded("I-OLD");
        old.RenewsAt = DateTime.UtcNow.AddHours(12);
        await db.SaveChangesAsync();
        SetSubscription("I-NEW", "ACTIVE", termEnd);

        Assert.That(await service.PayPalSubscriptionChanged("I-NEW"), Is.Empty); // refused; PayPal refuses the cancellation (no reply)

        var refused = await Loaded("I-NEW");
        Assert.That((refused.ContinuationRefusedAt != null, refused.Status), Is.EqualTo((true, "active")));
        paypal.Replies["/v1/billing/subscriptions/I-NEW/cancel"] = new JObject();
        paypal.Bodies.Clear();
        db.ChangeTracker.Clear();

        await service.ConvertDueRenewals();

        Assert.That(paypal.Bodies.Keys, Is.EqualTo(new[] { "/v1/billing/subscriptions/I-NEW/cancel" }), "cancelled, the old subscription untouched");
        db.ChangeTracker.Clear();
        Assert.That((await Loaded("I-NEW")).Status, Is.EqualTo("cancelled"));
        Assert.That((await Loaded("I-OLD")).Status, Is.EqualTo("active"));
        paypal.Bodies.Clear();
        await service.ConvertDueRenewals();
        Assert.That(paypal.Bodies, Is.Empty, "not retried once cancelled");
    }

    [Test]
    public async Task HourlyRunLeavesAnExtensionAwaitingApprovalAlone()
    {
        await RequestExtension();

        await service.ConvertDueRenewals();

        Assert.That(await db.Subscriptions.AnyAsync(s => s.ExternalId == "I-NEW"), Is.False);
        Assert.That((await Loaded("I-OLD")).Status, Is.EqualTo("active"));
    }

    [Test]
    public void PayPalExtensionNeedsReturnUrls()
    {
        var request = Extension();
        request.ReturnUrl = null;
        Assert.ThrowsAsync<ApiException>(() => service.ExtendLongTerm("buyer", "I-OLD", request));
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
