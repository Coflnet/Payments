using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Payments.Controllers;

public class RevenueExportTests : DirectSubscriptionTestBase
{
    private static readonly DateTime Start = DateTime.UtcNow.AddDays(-10);
    private static readonly DateTime End = DateTime.UtcNow.AddDays(-1);
    private static readonly string[] OriginalColumns =
    [
        "TransactionId", "UserId", "UserCountry", "ZipCode", "State", "Provider", "PaymentMethod", "Amount", "Subtotal", "DiscountAmount",
        "TaxAmount", "TaxRate", "TaxRemittedByProcessor", "NetAmount", "ProcessorFee", "CreatorCode", "CreatorCodeDiscount", "Currency",
        "ProductId", "CoinAmount", "Timestamp", "Status", "ExternalReference", "ExternalTransactionId", "Locale", "IsSubscription", "BuyerEmail"
    ];

    private RevenueExportController Controller() => new(NullLogger<RevenueExportController>.Instance, db);

    private PaymentRecord Record(string order, PaymentRecordStatus status = PaymentRecordStatus.Confirmed, string provider = "stripe",
        bool subscription = true, bool managed = false) => new()
    {
        UserId = 1, ExternalUserId = "buyer", Country = "DE", GrossAmount = 11.53m, Currency = "EUR", Provider = provider,
        ExternalOrderId = order, PaidAt = DateTime.UtcNow.AddDays(-5), Status = status, IsSubscriptionPayment = subscription,
        TaxRemittedByProcessor = managed, CheckoutCountry = "DE", BillingCountry = "AT", PaymentMethodCountry = "FR", IpAddress = "203.0.113.7"
    };

    private async Task<(string[] header, List<Dictionary<string, string>> rows)> Csv(bool includeAll)
    {
        var result = await Controller().ExportTransactions(Start, End, format: "csv", includeAllStatuses: includeAll) as FileContentResult;
        var lines = Encoding.UTF8.GetString(result.FileContents).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var header = lines[0].Split(',');
        var rows = lines.Skip(1).Select(l => header.Zip(l.Split(','), (h, v) => (h, v: v.Trim('"'))).ToDictionary(x => x.h, x => x.v)).ToList();
        return (header, rows);
    }

    private async Task SeedAllStatuses()
    {
        await users.GetOrCreate("buyer");
        db.PaymentRecords.AddRange(Record("in_ok"), Record("in_ref", PaymentRecordStatus.Refunded),
            Record("in_part", PaymentRecordStatus.PartiallyRefunded), Record("in_disp", PaymentRecordStatus.Disputed),
            Record("in_mor", managed: true), Record("SALE-1", provider: "paypal"), Record("ls_1", provider: "lemonsqueezy"),
            Record("one_time", subscription: false), Record("GPA.1", provider: "googlepay", managed: true));
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task DefaultExportStaysConfirmedOnlyWithTheOriginalColumnsFirstAndTheEvidenceColumnsAppended()
    {
        await SeedAllStatuses();

        var (header, rows) = await Csv(false);

        Assert.That(header.Take(OriginalColumns.Length), Is.EqualTo(OriginalColumns));
        Assert.That(header.Skip(OriginalColumns.Length), Is.EqualTo(
            new[] { "CheckoutCountry", "BillingCountry", "PaymentMethodCountry", "PayerCountry", "IpAddress", "SubscriptionMode" }));
        Assert.That(rows.Select(r => r["Status"]).Distinct(), Is.EqualTo(new[] { "Confirmed" }));
        Assert.That(rows, Has.Count.EqualTo(6));
    }

    [Test]
    public async Task IncludeAllStatusesExportsEveryRecordStatus()
    {
        await SeedAllStatuses();

        var (_, rows) = await Csv(true);

        Assert.That(rows.Select(r => r["Status"]).Distinct().Order(), Is.EqualTo(new[] { "Confirmed", "Disputed", "PartiallyRefunded", "Refunded" }));
        Assert.That(rows, Has.Count.EqualTo(9));
    }

    [Test]
    public async Task EvidenceColumnsAndSubscriptionModeAreExported()
    {
        await SeedAllStatuses();

        var (_, rows) = await Csv(true);

        string Mode(string order) => rows.Single(r => r["ExternalReference"] == order)["SubscriptionMode"];
        Assert.That((Mode("in_ok"), Mode("in_mor"), Mode("SALE-1"), Mode("ls_1"), Mode("one_time")),
            Is.EqualTo(("direct", "managed", "direct", "lemonsqueezy", "")));
        // Regression: Google Play subscription payments (TaxRemittedByProcessor) were labelled "managed", i.e. as Stripe
        // Managed Payments, in the VAT export.
        Assert.That(Mode("GPA.1"), Is.EqualTo("googlepay"));
        var stripe = rows.Single(r => r["ExternalReference"] == "in_ok");
        Assert.That((stripe["CheckoutCountry"], stripe["BillingCountry"], stripe["PaymentMethodCountry"], stripe["PayerCountry"], stripe["IpAddress"]),
            Is.EqualTo(("DE", "AT", "FR", "", "203.0.113.7")));
        var paypal = rows.Single(r => r["ExternalReference"] == "SALE-1");
        Assert.That((paypal["BillingCountry"], paypal["PayerCountry"]), Is.EqualTo(("", "AT")), "PayPal's payer country is its own column");
    }

    /// <summary>
    /// Regression: a captured payment marked REFUND_PENDING has no PaymentRecord and was missing from the export, so the
    /// VAT report could not see money the provider took. Exported on request with its sale id and checkout country,
    /// unless a record exists for it.
    /// </summary>
    [Test]
    public async Task RefundPendingPaymentsWithoutARecordAreExportedWhenAllStatusesAreRequested()
    {
        await SeedAllStatuses();
        var user = await users.GetOrCreate("buyer");
        db.PaymentRequests.AddRange(
            new PaymentRequest { Provider = "paypal", SessionId = "SALE-9", Amount = 11.53m, Country = "DE", User = user, ProductId = premium,
                State = PaymentRequest.Status.REFUND_PENDING, CreatedAt = DateTime.UtcNow.AddDays(-4), UpdatedAt = DateTime.UtcNow.AddDays(-4) },
            new PaymentRequest { Provider = "stripe", SessionId = "in_ok", Amount = 11.53m, Country = "DE", User = user,
                State = PaymentRequest.Status.REFUND_PENDING, CreatedAt = DateTime.UtcNow.AddDays(-4), UpdatedAt = DateTime.UtcNow.AddDays(-4) },
            new PaymentRequest { Provider = "paypal", SessionId = "I-REJ", Amount = 11.53m, Country = "DE", User = user,
                State = PaymentRequest.Status.REJECTED, CreatedAt = DateTime.UtcNow.AddDays(-4), UpdatedAt = DateTime.UtcNow.AddDays(-4) });
        await db.SaveChangesAsync();

        var (_, withoutAll) = await Csv(false);
        var (_, all) = await Csv(true);

        Assert.That(withoutAll.Select(r => r["ExternalReference"]), Does.Not.Contain("SALE-9"));
        var pending = all.Single(r => r["Status"] == "RefundPending");
        Assert.That((pending["ExternalReference"], pending["CheckoutCountry"], pending["Provider"], pending["UserId"], pending["Amount"]),
            Is.EqualTo(("SALE-9", "DE", "paypal", "buyer", "11.53")));
        Assert.That(all.Select(r => r["ExternalReference"]), Does.Not.Contain("I-REJ"), "a rejected checkout took no money");
        Assert.That(all.Count(r => r["ExternalReference"] == "in_ok"), Is.EqualTo(1), "a payment with a record is not listed twice");
    }

    /// <summary>
    /// Regression: the CSV formatted numbers with the current culture; under de-DE "11.53" became "11,53" and split into two
    /// columns, shifting every later one.
    /// </summary>
    [Test]
    public async Task CsvNumbersAndTimestampsAreCultureInvariant()
    {
        await SeedAllStatuses();
        var record = db.PaymentRecords.Single(r => r.ExternalOrderId == "in_ok");
        record.TaxRate = 19.5;
        record.TaxAmount = 1.84m;
        record.PaidAt = new DateTime(2026, 11, 12, 13, 4, 5, DateTimeKind.Utc);
        await db.SaveChangesAsync();
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var (header, rows) = await Csv(true);

            var row = rows.Single(r => r["ExternalReference"] == "in_ok");
            Assert.That((row["Amount"], row["TaxAmount"], row["TaxRate"], row["Timestamp"]),
                Is.EqualTo(("11.53", "1.84", "19.5", "2026-11-12 13:04:05")));
            Assert.That(row["SubscriptionMode"], Is.EqualTo("direct"), "no column shifted");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    /// <summary>
    /// Regression: one-time checkouts marked REFUND_PENDING by the callback controller never had UpdatedAt set (0001-01-01),
    /// so the export's date range never matched them. They fall back to the checkout start.
    /// </summary>
    [Test]
    public async Task RefundPendingOneTimePaymentsWithoutUpdateTimeUseTheirCreation()
    {
        var user = await users.GetOrCreate("buyer");
        var created = DateTime.UtcNow.AddDays(-3);
        db.PaymentRequests.Add(new PaymentRequest { Provider = "stripe", SessionId = "pi_1", Amount = 6.69m, Country = "TR", User = user,
            State = PaymentRequest.Status.REFUND_PENDING, CreatedAt = created });
        await db.SaveChangesAsync();

        var export = (await Controller().ExportTransactions(Start, End, includeAllStatuses: true) as OkObjectResult).Value as List<DetailedTransactionExport>;

        var pending = export.Single(t => t.ExternalReference == "pi_1");
        Assert.That((pending.Status, pending.Timestamp), Is.EqualTo(("RefundPending", created)));
    }

    [Test]
    public async Task JsonEndpointsExposeTheSameFields()
    {
        await SeedAllStatuses();

        var detailed = ((await Controller().GetDetailedTransactions(Start, End)).Result as OkObjectResult).Value as List<DetailedTransactionExport>;
        var export = (await Controller().ExportTransactions(Start, End, includeAllStatuses: true) as OkObjectResult).Value as List<DetailedTransactionExport>;

        foreach (var list in new[] { detailed, export })
        {
            var stripe = list.Single(t => t.ExternalReference == "in_ok");
            Assert.That((stripe.CheckoutCountry, stripe.BillingCountry, stripe.PaymentMethodCountry, stripe.IpAddress, stripe.SubscriptionMode),
                Is.EqualTo(("DE", "AT", "FR", "203.0.113.7", "direct")));
            Assert.That(list.Single(t => t.ExternalReference == "SALE-1").PayerCountry, Is.EqualTo("AT"));
        }
    }

    [Test]
    public void StatusMappingCoversTheUnfulfilledRequestStates()
    {
        Assert.That(ExportStatus.FromRequest(PaymentRequest.Status.REFUND_PENDING), Is.EqualTo("RefundPending"));
        Assert.That(ExportStatus.FromRequest(PaymentRequest.Status.REJECTED), Is.EqualTo("Rejected"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExportStatus.FromRequest(PaymentRequest.Status.PAID));
    }
}
