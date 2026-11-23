using System.Threading.Tasks;
using Google.Apis.AndroidPublisher.v3.Data;

namespace Coflnet.Payments.Services;

/// <summary>Android Publisher operations used by Play checkout and reconciliation.</summary>
public interface IGooglePlayPublisher
{
    Task<SubscriptionPurchaseV2> GetSubscriptionV2(string token);

    /// <summary>Returns the final amount paid and the tax included in it.</summary>
    Task<(Money Total, Money Tax)> GetOrderAmounts(string orderId);

    Task AcknowledgeSubscription(string productId, string token);
    Task<bool> IsVoidedPurchase(string orderId, string purchaseToken);
    Task<ProductPurchase> VerifyProductPurchaseAsync(string packageName, string productId, string purchaseToken);
    Task AcknowledgeProductPurchaseAsync(string packageName, string productId, string purchaseToken);
    Task<InAppProduct> GetProductDetailsAsync(string packageName, string productId);
}
