using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services.Subscriptions;
using Google;
using Google.Apis.AndroidPublisher.v3.Data;
using Microsoft.Extensions.Logging;

namespace Coflnet.Payments.Services;

public partial class SubscriptionService
{
    private readonly Lazy<Dictionary<int, string>> googlePlaySaleOffers;

    private Dictionary<int, string> ReadGooglePlaySaleOffers()
    {
        var section = googlePlayConfig?.GetSection("SALE_OFFERS");
        if (section == null)
            return new();

        Dictionary<int, string> offers;
        try
        {
            offers = section.Value is string json
                ? ParseGooglePlaySaleOffers(json)
                : ParseGooglePlaySaleOffers(section.GetChildren().Select(child =>
                    (child.Key, child.GetChildren().Any() ? null : child.Value)));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException)
        {
            offers = null;
        }

        if (offers == null)
            logger.LogError("Invalid GOOGLEPAY:SALE_OFFERS configuration");
        return offers;
    }

    private static Dictionary<int, string> ParseGooglePlaySaleOffers(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new();
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return null;
        return ParseGooglePlaySaleOffers(document.RootElement.EnumerateObject().Select(entry =>
            (entry.Name, entry.Value.ValueKind == JsonValueKind.String ? entry.Value.GetString() : null)));
    }

    private static Dictionary<int, string> ParseGooglePlaySaleOffers(IEnumerable<(string Key, string Id)> entries)
    {
        var offers = new Dictionary<int, string>();
        var ids = new HashSet<string>();
        foreach (var (key, id) in entries)
        {
            if (!TryParseGooglePlayOfferPercent(key, out var percent) || !IsValidGooglePlayOfferId(id))
                return null;
            if (!ids.Add(id) || !offers.TryAdd(percent, id))
                return null;
        }
        return offers;
    }

    private static bool TryParseGooglePlayOfferPercent(string key, out int percent) =>
        int.TryParse(key, CultureInfo.InvariantCulture, out percent) && percent is >= 1 and <= 99 && key.All(char.IsAsciiDigit);

    private static bool IsValidGooglePlayOfferId(string id) => id != null && Regex.IsMatch(id, @"\A[a-z0-9][a-z0-9-]{0,62}\z");

    private async Task<(string OfferId, int? DiscountPercent)> GooglePlaySaleOffer()
    {
        var offers = googlePlaySaleOffers.Value;
        if (offers is not { Count: > 0 })
            return (null, null);
        var percent = GooglePlaySalePercent(await DiscountCodes.GetActiveAutoApply());
        return percent is int p && offers.TryGetValue(p, out var offerId) ? (offerId, p) : (null, null);
    }

    private static int? GooglePlaySalePercent(DiscountCode sale)
    {
        if (sale is not { AmountType: "percent", Duration: DiscountCode.FirstPaymentOnly, StartsAt: not null, MaxRedemptions: null })
            return null;
        var amount = sale.AmountForWeeks(4);
        return amount is > 0 and < 100 && amount == decimal.Truncate(amount) ? (int)amount : null;
    }

    private async Task<(string SaleCode, bool Allowed)> ResolveGooglePlayOffer(string id, string offerId, SubscriptionPurchaseV2 purchase,
        UserSubscription existing)
    {
        if (string.IsNullOrEmpty(offerId))
            return (null, true);
        if (existing != null)
            return string.IsNullOrEmpty(existing.DiscountCode) ? RejectGooglePlayOffer(id) : (existing.DiscountCode, true);

        var offers = googlePlaySaleOffers.Value;
        if (offers == null)
            return (null, false);

        var start = purchase.StartTimeDateTimeOffset?.UtcDateTime;
        // Play omits the start time while pending, so use the sale running at first validation.
        if (purchase.SubscriptionState == "SUBSCRIPTION_STATE_PENDING")
            start ??= DateTime.UtcNow;
        var percent = offers.FirstOrDefault(entry => entry.Value == offerId).Key;
        if (start == null || percent == 0)
            return RejectGooglePlayOffer(id);
        var sale = await FindGooglePlaySale(percent, start.Value);
        return sale == null ? RejectGooglePlayOffer(id) : (sale.Code, true);
    }

    private (string SaleCode, bool Allowed) RejectGooglePlayOffer(string id)
    {
        logger.LogError("Google Play subscription {SubscriptionId} uses an unsupported offer", id);
        return (null, false);
    }

    private async Task<DiscountCode> FindGooglePlaySale(int percent, DateTime start)
    {
        var codes = DiscountCodes;
        var sale = await codes.GetAutoApplyAt(start);
        if (GooglePlaySalePercent(sale) == percent)
            return sale;
        sale = await codes.GetAutoApplyAt(start.AddMinutes(-15));
        return GooglePlaySalePercent(sale) == percent ? sale : null;
    }

    private static decimal GooglePlayAmount(Money price) =>
        (price?.Units ?? 0) + (price?.Nanos ?? 0) / 1_000_000_000m;

    private async Task<PaymentRecord> BuildGooglePlayPayment(UserSubscription local, TopUpProduct plan, SubscriptionPurchaseV2 purchase,
        string order, decimal amount, bool hasOffer, bool first)
    {
        var paid = hasOffer ? await LookupGooglePlayOrder(order) : null;
        if (purchase.TestPurchase != null)
        {
            if (hasOffer)
                logger.LogInformation("Google Play test offer order lookup succeeded {Succeeded}", paid.HasValue);
            return null;
        }

        var currency = paid?.Currency ?? local.Currency ?? plan.CurrencyCode.ToUpperInvariant();
        var gross = paid?.Gross ?? amount;
        if (!paid.HasValue && hasOffer && first)
            gross = await EstimateGooglePlayOffer(local.DiscountCode, amount, currency);
        var taxAmount = paid?.Tax ?? 0;
        var paidAt = first ? purchase.StartTimeDateTimeOffset?.UtcDateTime : null;
        return new PaymentRecord
        {
            User = local.User, ExternalUserId = local.User.ExternalId, Country = local.CheckoutCountry,
            Provider = PlayProvider, PaymentMethod = PlayProvider, ExternalOrderId = order, ExternalTransactionId = local.ExternalId,
            ProductId = plan.Id, ProductSlug = plan.Slug, CoinAmount = (long)plan.Cost,
            GrossAmount = gross, TaxAmount = taxAmount, Subtotal = gross - taxAmount, NetAmount = gross - taxAmount,
            Currency = currency, TaxRemittedByProcessor = true, IsSubscriptionPayment = true,
            PaidAt = paidAt ?? DateTime.UtcNow, RecordedAt = DateTime.UtcNow, Status = PaymentRecordStatus.Confirmed
        };
    }

    private async Task<(decimal Gross, decimal Tax, string Currency)?> LookupGooglePlayOrder(string order)
    {
        try
        {
            var (total, tax) = await googlePlay.GetOrderAmounts(order);
            if (string.IsNullOrWhiteSpace(total?.CurrencyCode))
            {
                logger.LogError("Google Play offer order lookup returned no usable total");
                return null;
            }
            var gross = GooglePlayAmount(total);
            return (gross, GooglePlayOrderTax(tax, total.CurrencyCode, gross), total.CurrencyCode);
        }
        catch (Exception e)
        {
            logger.LogError("Google Play offer order lookup failed ({ExceptionType}, HTTP {StatusCode})",
                e.GetType().Name, (int?)(e as GoogleApiException)?.HttpStatusCode);
            return null;
        }
    }

    private decimal GooglePlayOrderTax(Money tax, string currency, decimal gross)
    {
        if (tax == null)
            return 0;
        var amount = GooglePlayAmount(tax);
        if (tax.CurrencyCode == currency && amount >= 0 && amount <= gross)
            return amount;
        logger.LogError("Google Play offer order lookup returned invalid tax");
        return 0;
    }

    private async Task<decimal> EstimateGooglePlayOffer(string code, decimal recurring, string currency)
    {
        var sale = (await DiscountCodes.Terms(code))?.ForPeriod(4);
        return sale is { AmountType: "percent", Duration: DiscountCode.FirstPaymentOnly }
            ? RoundGooglePlayOfferAmount(recurring * (100 - sale.Amount) / 100, currency)
            : recurring;
    }

    private static decimal RoundGooglePlayOfferAmount(decimal amount, string currency) => currency.ToUpperInvariant() switch
    {
        // Play reports ISO 4217 amounts. These minor units differ from those in StripeAmounts.
        "ISK" or "UGX" => decimal.Round(amount, 0, MidpointRounding.AwayFromZero),
        "MGA" => decimal.Round(amount, 2, MidpointRounding.AwayFromZero),
        "IQD" or "LYD" => decimal.Round(amount, 3, MidpointRounding.AwayFromZero),
        "CLF" or "UYW" => decimal.Round(amount, 4, MidpointRounding.AwayFromZero),
        _ => StripeAmounts.FromMinorUnits(StripeAmounts.ToMinorUnits(amount, currency), currency)
    };

    private async Task RedeemGooglePlaySale(string subscriptionId, string code)
    {
        try
        {
            if (await DiscountCodes.Redeem(code))
                return;
        }
        catch (Exception)
        {
            // The period has already been granted.
        }
        logger.LogWarning("Could not count Google Play sale redemption for subscription {SubscriptionId}", subscriptionId);
    }
}
