using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using PayPalCheckoutSdk.Core;
using PayPalHttp;

namespace Payments.Controllers;

public class PaypalSecurityTests
{
    private SqliteConnection connection;
    private PaymentContext db;
    private TransactionService transactions;
    private FakePayPal paypal;
    private User sender;
    private User receiver;
    private TopUpProduct topup;
    private const string RefundEvent = """
        {"id":"EVENT","event_type":"PAYMENT.CAPTURE.REFUNDED","resource":{"id":"REFUND",
         "links":[{"rel":"up","href":"https://attacker.invalid/transfer-reference"}]}}
        """;
    private const string VerifyPath = "/v1/notifications/verify-webhook-signature";

    [SetUp]
    public async Task Setup()
    {
        connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        sender = new User { ExternalId = "sender", Balance = 99 };
        receiver = new User { ExternalId = "receiver", Balance = 1 };
        topup = new TopUpProduct { Slug = "paypal-coins", ProviderSlug = "paypal", Type = Product.ProductType.TOP_UP, Cost = 100, CurrencyCode = "EUR" };
        db.AddRange(sender, receiver, topup, new PurchaseableProduct { Slug = "revert" });
        await db.SaveChangesAsync();
        transactions = new TransactionService(NullLogger<TransactionService>.Instance, db,
            new UserService(NullLogger<UserService>.Instance, db), new NullEvents(), null,
            new RuleEngine(NullLogger<RuleEngine>.Instance, db));
        paypal = new FakePayPal();
        paypal.Replies[VerifyPath] = JObject.Parse("""{"verification_status":"SUCCESS"}""");
        SetRefund("10.00", "REFUNDED");
    }

    [TearDown]
    public async Task TearDown()
    {
        await db.DisposeAsync();
        await connection.DisposeAsync();
    }

    private void SetRefund(string amount, string status)
    {
        paypal.Replies["/v2/payments/refunds/REFUND"] = JObject.Parse($$$"""
        {"id":"REFUND","status":"COMPLETED","amount":{"value":"{{{amount}}}","currency_code":"EUR"},
         "seller_payable_breakdown":{"total_refunded_amount":{"value":"{{{amount}}}","currency_code":"EUR"}},
         "links":[{"rel":"up","href":"https://api.paypal.com/v2/payments/captures/CAPTURE"}]}
        """);
        paypal.Replies["/v2/payments/captures/CAPTURE"] = JObject.Parse($$$"""
        {"id":"CAPTURE","status":"{{{status}}}","amount":{"value":"10.00","currency_code":"EUR"}}
        """);
    }

    private CallbackController Controller(string json = null, string webhookId = "configured-webhook")
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["LEMONSQUEEZY:SECRET"] = "test", ["PAYPAL:WEBHOOK_ID"] = webhookId
        }).Build();
        var controller = new CallbackController(config, NullLogger<CallbackController>.Instance, db, transactions,
            paypal, new NullEvents(), null, NullLogger<GooglePayController>.Instance, null, null, null, null);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        controller.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json ?? RefundEvent));
        foreach (var header in new[] { "AUTH-ALGO", "CERT-URL", "TRANSMISSION-ID", "TRANSMISSION-SIG", "TRANSMISSION-TIME" })
            controller.Request.Headers["PAYPAL-" + header] = "test-" + header;
        return controller;
    }

    private async Task SeedCredit(Product product = null, decimal amount = 100)
    {
        db.FiniteTransactions.Add(new FiniteTransaction { Product = product ?? topup, User = sender, Amount = amount, Reference = "CAPTURE" });
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task UnsignedReportedExploitCannotReverseEitherTransferLeg()
    {
        var transfer = new PurchaseableProduct { Slug = "transfer", Type = Product.ProductType.TOP_UP | Product.ProductType.VARIABLE_PRICE };
        await SeedCredit(transfer, -1);
        db.FiniteTransactions.Add(new FiniteTransaction { Product = transfer, User = receiver, Amount = 1, Reference = "CAPTURE" });
        await db.SaveChangesAsync();
        var controller = Controller("""
        {"id":"marker","event_type":"PAYMENT.CAPTURE.REFUNDED","resource":{"links":[
        {"rel":"up","href":"https://api.paypal.com/v2/payments/captures/CAPTURE","method":"GET"}]}}
        """);
        controller.Request.Headers.Clear();
        Assert.That(await controller.Paypal(), Is.TypeOf<UnauthorizedResult>());
        Assert.That(paypal.Calls, Is.Empty);
        Assert.That(sender.Balance, Is.EqualTo(99));
        Assert.That(receiver.Balance, Is.EqualTo(1));
        Assert.That(await db.FiniteTransactions.CountAsync(), Is.EqualTo(2));
    }

    [TestCase("AUTH-ALGO")]
    [TestCase("CERT-URL")]
    [TestCase("TRANSMISSION-ID")]
    [TestCase("TRANSMISSION-SIG")]
    [TestCase("TRANSMISSION-TIME")]
    public async Task EverySignatureHeaderIsRequired(string header)
    {
        var controller = Controller();
        controller.Request.Headers.Remove("PAYPAL-" + header);
        Assert.That(await controller.Paypal(), Is.TypeOf<UnauthorizedResult>());
        Assert.That(paypal.Calls, Is.Empty);
    }

    [TestCase("")]
    [TestCase(null)]
    public async Task MissingWebhookConfigurationFailsClosed(string webhookId)
    {
        Assert.That(await Controller(webhookId: webhookId).Paypal(), Is.InstanceOf<StatusCodeResult>().With.Property("StatusCode").EqualTo(400));
        Assert.That(paypal.Calls, Is.Empty);
    }

    [TestCase("FAILURE")]
    [TestCase(null)]
    public async Task FailedVerificationCannotReachPaymentApi(string status)
    {
        paypal.Replies[VerifyPath]["verification_status"] = status;
        Assert.That(await Controller().Paypal(), Is.TypeOf<UnauthorizedResult>());
        Assert.That(paypal.Calls, Is.EqualTo(new[] { VerifyPath }));
        Assert.That(await db.FiniteTransactions.CountAsync(), Is.Zero);
    }

    [TestCase(VerifyPath)]
    [TestCase("/v2/payments/refunds/REFUND")]
    [TestCase("/v2/payments/captures/CAPTURE")]
    public async Task ApiFailureNeverChangesBalance(string failedPath)
    {
        await SeedCredit();
        paypal.Replies.Remove(failedPath);
        Assert.That(await Controller().Paypal(), Is.InstanceOf<StatusCodeResult>().With.Property("StatusCode").EqualTo(400));
        Assert.That(paypal.Calls.Last(), Is.EqualTo(failedPath));
        Assert.That(sender.Balance, Is.EqualTo(99));
        Assert.That(await db.FiniteTransactions.CountAsync(), Is.EqualTo(1));
    }

    [TestCase("stripe", 100)]
    [TestCase("paypal", -1)]
    public async Task RefundRequiresPositivePaypalTopup(string provider, int amount)
    {
        topup.ProviderSlug = provider;
        await SeedCredit(amount: amount);
        Assert.That(await Controller().Paypal(), Is.TypeOf<BadRequestResult>());
        Assert.That(sender.Balance, Is.EqualTo(99));
        Assert.That(await db.FiniteTransactions.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task EvenVerifiedRefundCannotReverseTransferCreditOrDebit()
    {
        var transfer = new PurchaseableProduct { Slug = "transfer", Type = Product.ProductType.TOP_UP };
        await SeedCredit(transfer, -1);
        db.FiniteTransactions.Add(new FiniteTransaction { Product = transfer, User = receiver, Amount = 1, Reference = "CAPTURE" });
        await db.SaveChangesAsync();
        Assert.That(await Controller().Paypal(), Is.TypeOf<BadRequestResult>());
        Assert.That(sender.Balance + receiver.Balance, Is.EqualTo(100));
        Assert.That(await db.FiniteTransactions.CountAsync(), Is.EqualTo(2));
    }

    [TestCase("PENDING", "REFUNDED")]
    [TestCase("FAILED", "REFUNDED")]
    [TestCase("COMPLETED", "COMPLETED")]
    public async Task BothRefundAndCaptureMustConfirmRefund(string refundStatus, string captureStatus)
    {
        await SeedCredit();
        SetRefund("10.00", captureStatus);
        paypal.Replies["/v2/payments/refunds/REFUND"]["status"] = refundStatus;
        Assert.That(await Controller().Paypal(), Is.TypeOf<BadRequestResult>());
        Assert.That(sender.Balance, Is.EqualTo(99));
    }

    [Test]
    public async Task VerifiedPartialRefundsAreDebitOnlyCumulativeAndReplaySafe()
    {
        await SeedCredit();
        SetRefund("2.50", "PARTIALLY_REFUNDED");
        Assert.That(await Controller().Paypal(), Is.TypeOf<OkResult>());
        Assert.That(sender.Balance, Is.EqualTo(74));
        Assert.That(paypal.VerificationBody["webhook_id"]?.Value<string>(), Is.EqualTo("configured-webhook"));
        Assert.That(paypal.VerificationBody.SelectToken("webhook_event.resource.links[0].href")?.Value<string>(),
            Is.EqualTo("https://attacker.invalid/transfer-reference"));
        Assert.That(paypal.VerificationBody["transmission_sig"]?.Value<string>(), Is.EqualTo("test-TRANSMISSION-SIG"));
        Assert.That(await Controller(RefundEvent.Replace("EVENT", "ANOTHER-EVENT")).Paypal(), Is.TypeOf<OkResult>());
        SetRefund("5.00", "PARTIALLY_REFUNDED");
        Assert.That(await Controller().Paypal(), Is.TypeOf<OkResult>());
        SetRefund("2.50", "PARTIALLY_REFUNDED");
        Assert.That(await Controller().Paypal(), Is.TypeOf<OkResult>());
        Assert.That(sender.Balance, Is.EqualTo(49));
        SetRefund("10.00", "REFUNDED");
        Assert.That(await Controller().Paypal(), Is.TypeOf<OkResult>());
        Assert.That(await Controller().Paypal(), Is.TypeOf<OkResult>());
        Assert.That(sender.Balance, Is.EqualTo(-1)); // Spent coins do not prevent recovery.
        Assert.That(receiver.Balance, Is.EqualTo(1));
        var refunds = await db.FiniteTransactions.Where(t => t.Product.Slug == "revert").ToListAsync();
        Assert.That(refunds.Count, Is.EqualTo(3));
        Assert.That(refunds.All(t => t.Amount < 0), Is.True);
        Assert.That(refunds.Sum(t => t.Amount), Is.EqualTo(-100));
    }

    [Test]
    public async Task CompletedCallbackCannotRewriteTransferReferenceBeforeApiValidation()
    {
        var transfer = new PurchaseableProduct { Slug = "transfer", Type = Product.ProductType.TOP_UP };
        await SeedCredit(transfer, -1);
        paypal.Replies.Remove("/v2/payments/captures/CAPTURE");
        var result = await Controller("""
        {"id":"EVENT","event_type":"PAYMENT.CAPTURE.COMPLETED","resource":{"id":"CAPTURE",
         "supplementary_data":{"related_ids":{"order_id":"CAPTURE"}},
         "links":[{"rel":"self","href":"https://api.paypal.com/v2/payments/captures/FAKE"}]}}
        """).Paypal();
        Assert.That(result, Is.InstanceOf<StatusCodeResult>().With.Property("StatusCode").EqualTo(400));
        Assert.That((await db.FiniteTransactions.SingleAsync()).Reference, Is.EqualTo("CAPTURE"));
    }

    [Test]
    public async Task SignatureVerificationPreservesOriginalEventJson()
    {
        const string payload = """
        { "id":"EVENT", "event_type":"ignored", "create_time":"2026-09-13T22:11:00.000+01:00",
          "resource":{"id":"CAPTURE"}, "unknown_extension":{"precise_number":1.0000} }
        """;
        Assert.That(await Controller(payload).Paypal(), Is.TypeOf<OkResult>());
        Assert.That(paypal.VerificationJson, Does.Contain(payload));
        Assert.That(paypal.Calls, Is.EqualTo(new[] { VerifyPath }));
    }

    [Test]
    public async Task AmbiguousDuplicateJsonFieldsAreRejected()
    {
        var result = await Controller("""{"id":"EVENT","resource":{"id":"CAPTURE","id":"OTHER"}}""").Paypal();
        Assert.That(result, Is.InstanceOf<StatusCodeResult>().With.Property("StatusCode").EqualTo(400));
        Assert.That(paypal.Calls, Is.Empty);
    }

    private const string Completion = """
        {"id":"EVENT","event_type":"PAYMENT.CAPTURE.COMPLETED","resource":{"id":"CAPTURE",
         "supplementary_data":{"related_ids":{"order_id":"ORDER"}},
         "links":[{"rel":"self","href":"https://attacker.invalid/FAKE"}]}}
        """;

    private void SetOrder(string status = "COMPLETED")
    {
        paypal.Replies["/v2/checkout/orders/ORDER"] = JObject.Parse($$$"""
        {"id":"ORDER","status":"{{{status}}}",
         "payer":{"email_address":"buyer@example.invalid","name":{"given_name":"Test","surname":"Buyer"},
                  "address":{"country_code":"DE","postal_code":"10115"}},
         "purchase_units":[{"reference_id":"sender","custom_id":"{{{topup.Id}}};100;sender",
           "amount":{"value":"10.00","currency_code":"EUR"},
           "shipping":{"name":{"full_name":"Test Buyer"},"address":{"country_code":"DE","postal_code":"10115"}},
           "payments":{"captures":[{"id":"CAPTURE","status":"COMPLETED","amount":{"value":"10.00","currency_code":"EUR"}}]}}]}
        """);
    }

    [Test]
    public async Task CompletionCreditsApiSelectedAccountOnceAndIgnoresCallbackLinks()
    {
        SetRefund("0", "COMPLETED");
        SetOrder();
        Assert.That(await Controller(Completion).Paypal(), Is.TypeOf<OkResult>());
        Assert.That(await Controller(Completion.Replace("EVENT", "OTHER-EVENT")).Paypal(), Is.TypeOf<OkResult>());
        Assert.That(sender.Balance, Is.EqualTo(199));
        Assert.That(receiver.Balance, Is.EqualTo(1));
        Assert.That((await db.FiniteTransactions.SingleAsync()).Reference, Is.EqualTo("CAPTURE"));
        Assert.That((await db.PaymentRecords.SingleAsync()).Provider, Is.EqualTo("paypal"));
    }

    [TestCase("id", "OTHER-CAPTURE")]
    [TestCase("status", "REFUNDED")]
    public async Task OrderMustConfirmTheSameCompletedCapture(string field, string value)
    {
        SetRefund("0", "COMPLETED");
        SetOrder();
        paypal.Replies["/v2/checkout/orders/ORDER"]["purchase_units"][0]["payments"]["captures"][0][field] = value;
        Assert.That(await Controller(Completion).Paypal(), Is.TypeOf<BadRequestResult>());
        Assert.That(sender.Balance, Is.EqualTo(99));
        Assert.That(await db.FiniteTransactions.CountAsync(), Is.Zero);
    }

    [TestCase("REFUNDED")]
    [TestCase("PARTIALLY_REFUNDED")]
    public async Task DelayedCompletionCannotCreditRefundedPayment(string status)
    {
        SetRefund("10", status);
        Assert.That(await Controller(Completion).Paypal(), Is.TypeOf<OkResult>());
        Assert.That(sender.Balance, Is.EqualTo(99));
        Assert.That(await db.FiniteTransactions.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task ApprovalUsesApiOrderBeforeUpdatingAccountOrCapturing()
    {
        SetOrder("APPROVED");
        paypal.Replies["/v2/checkout/orders/ORDER/capture"] = JObject.Parse("""{"id":"ORDER","status":"COMPLETED"}""");
        var result = await Controller("""
        {"id":"EVENT","event_type":"CHECKOUT.ORDER.APPROVED","resource":{"id":"ORDER",
         "payer":{"address":{"country_code":"XX"}},"purchase_units":[{"custom_id":"1;99999;receiver"}]}}
        """).Paypal();
        Assert.That(result, Is.TypeOf<OkResult>());
        Assert.That(sender.Country, Is.EqualTo("DE"));
        Assert.That(receiver.Country, Is.Null);
        Assert.That(paypal.Calls, Is.EqualTo(new[] { VerifyPath, "/v2/checkout/orders/ORDER", "/v2/checkout/orders/ORDER/capture" }));
        Assert.That(await db.FiniteTransactions.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task RefundCurrencyMismatchCannotDebitBalance()
    {
        await SeedCredit();
        SetRefund("2.50", "PARTIALLY_REFUNDED");
        paypal.Replies["/v2/payments/refunds/REFUND"]["seller_payable_breakdown"]["total_refunded_amount"]["currency_code"] = "USD";
        Assert.That(await Controller().Paypal(), Is.TypeOf<BadRequestResult>());
        Assert.That(sender.Balance, Is.EqualTo(99));
    }

    private sealed class FakePayPal : PayPalHttpClient
    {
        public Dictionary<string, JObject> Replies { get; } = new();
        public List<string> Calls { get; } = new();
        public JObject VerificationBody { get; private set; }
        public string VerificationJson { get; private set; }
        public FakePayPal() : base(new SandboxEnvironment("test", "test")) { }
        public override async Task<PayPalHttp.HttpResponse> Execute<T>(T request)
        {
            var path = request.Path.TrimEnd('?');
            Calls.Add(path);
            if (path == VerifyPath)
            {
                VerificationJson = await request.Content.ReadAsStringAsync();
                VerificationBody = JObject.Parse(VerificationJson);
            }
            if (!Replies.TryGetValue(path, out var reply))
                throw new HttpRequestException("Simulated PayPal API failure: " + request.Path);
            using var content = new StringContent(reply.ToString(), Encoding.UTF8, "application/json");
            var result = Encoder.DeserializeResponse(content, request.ResponseType);
            return new PayPalHttp.HttpResponse(content.Headers, HttpStatusCode.OK, result);
        }
    }

    private sealed class NullEvents : ITransactionEventProducer, IPaymentEventProducer
    {
        public Task ProduceEvent(TransactionEvent e) => Task.CompletedTask;
        public Task ProduceEvent(PaymentEvent e) => Task.CompletedTask;
    }
}
