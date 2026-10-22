using Coflnet.Payments.Models.LemonSqueezy;
using NUnit.Framework;

namespace Coflnet.Payments.Services;

public class LemonSqueezyCustomDataSignerTests
{
    private const string Secret = "signing-secret";

    private static CustomData Signed(string userId = "user-1", int productId = 153, long coins = 1800,
        string isSubscription = "False", string creatorCode = null, long variantId = 777, long priceCents = 969, string secret = Secret)
    {
        var signature = LemonSqueezyCustomDataSigner.Sign(secret, userId, productId, coins, isSubscription, creatorCode, variantId, priceCents);
        return new CustomData(userId, productId, coins, isSubscription, creatorCode, variantId, priceCents, signature);
    }

    [Test]
    public void SignedCustomDataVerifies()
    {
        Assert.That(LemonSqueezyCustomDataSigner.Verify(Secret, Signed()), Is.True);
        Assert.That(LemonSqueezyCustomDataSigner.Verify(Secret, Signed(creatorCode: "CREATOR")), Is.True);
    }

    [Test]
    public void EverySignedFieldIsCovered()
    {
        var valid = Signed();
        var tampered = new[]
        {
            new CustomData("other", valid.ProductId, valid.CoinAmount, valid.IsSubscription, null, valid.VariantId, valid.PriceCents, valid.Signature),
            new CustomData(valid.UserId, 999, valid.CoinAmount, valid.IsSubscription, null, valid.VariantId, valid.PriceCents, valid.Signature),
            new CustomData(valid.UserId, valid.ProductId, 2160000, valid.IsSubscription, null, valid.VariantId, valid.PriceCents, valid.Signature),
            new CustomData(valid.UserId, valid.ProductId, valid.CoinAmount, "True", null, valid.VariantId, valid.PriceCents, valid.Signature),
            new CustomData(valid.UserId, valid.ProductId, valid.CoinAmount, valid.IsSubscription, "CREATOR", valid.VariantId, valid.PriceCents, valid.Signature),
            new CustomData(valid.UserId, valid.ProductId, valid.CoinAmount, valid.IsSubscription, null, 1, valid.PriceCents, valid.Signature),
            new CustomData(valid.UserId, valid.ProductId, valid.CoinAmount, valid.IsSubscription, null, valid.VariantId, 1, valid.Signature),
        };
        foreach (var data in tampered)
            Assert.That(LemonSqueezyCustomDataSigner.Verify(Secret, data), Is.False);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not-hex")]
    [TestCase("00")]
    public void MissingOrMalformedSignatureFails(string signature)
    {
        var data = new CustomData("user-1", 153, 1800, "False", null, 777, 969, signature);
        Assert.That(LemonSqueezyCustomDataSigner.Verify(Secret, data), Is.False);
    }

    [Test]
    public void OtherSecretOrMissingSecretFails()
    {
        Assert.That(LemonSqueezyCustomDataSigner.Verify("other", Signed()), Is.False);
        Assert.That(LemonSqueezyCustomDataSigner.Verify(null, Signed()), Is.False);
        Assert.That(LemonSqueezyCustomDataSigner.Verify(Secret, null), Is.False);
    }
}
