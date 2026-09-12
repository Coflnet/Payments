namespace Coflnet.Payments.Models;

public record SubscriptionPlan(string ProductSlug, string Title, decimal Price, string CurrencyCode,
    long OwnershipSeconds, int SlotCount, bool IsUpgrade);

public record SubscriptionChangeResult(string Status, string RedirectUrl = null);
