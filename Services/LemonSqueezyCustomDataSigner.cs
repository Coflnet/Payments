using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Coflnet.Payments.Models.LemonSqueezy;
using Microsoft.Extensions.Configuration;

namespace Coflnet.Payments.Services;

/// <summary>
/// Signs the custom data attached to a Lemon Squeezy checkout so the webhook can tell our own
/// values apart from buyer-supplied ones. Lemon Squeezy lets anyone attach custom data to a
/// checkout through URL parameters (<c>checkout[custom][coin_amount]=...</c>), so an unsigned
/// user, product or coin amount in a webhook proves nothing.
/// </summary>
public static class LemonSqueezyCustomDataSigner
{
    private const string Version = "v1";

    /// <summary>
    /// Key used for signing; falls back to the webhook secret so no new configuration is required.
    /// </summary>
    public static string SecretFrom(IConfiguration config) =>
        config["LEMONSQUEEZY:CUSTOM_DATA_SECRET"] is { Length: > 0 } dedicated ? dedicated : config["LEMONSQUEEZY:SECRET"];

    public static string Sign(string secret, string userId, int productId, long coinAmount, string isSubscription,
        string creatorCode, long variantId, long priceCents)
    {
        if (string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException("LEMONSQUEEZY:SECRET is required to sign checkout custom data");
        var payload = string.Join("\n", Version, userId, productId.ToString(CultureInfo.InvariantCulture),
            coinAmount.ToString(CultureInfo.InvariantCulture), isSubscription, creatorCode ?? string.Empty,
            variantId.ToString(CultureInfo.InvariantCulture), priceCents.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload)));
    }

    /// <summary>
    /// True only when every identifying value in <paramref name="data"/> was signed by <see cref="Sign"/>.
    /// </summary>
    public static bool Verify(string secret, CustomData data)
    {
        if (data == null || string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(data.Signature))
            return false;
        byte[] provided;
        try
        {
            provided = Convert.FromHexString(data.Signature);
        }
        catch (FormatException)
        {
            return false;
        }
        var expected = Convert.FromHexString(Sign(secret, data.UserId ?? string.Empty, data.ProductId, data.CoinAmount,
            data.IsSubscription ?? string.Empty, data.CreatorCode, data.VariantId, data.PriceCents));
        return provided.Length == expected.Length && CryptographicOperations.FixedTimeEquals(expected, provided);
    }
}
