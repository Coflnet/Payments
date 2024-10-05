using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Models.LemonSqueezy;
using Microsoft.Extensions.Logging;

namespace Coflnet.Payments.Services;

public class SubscriptionService
{
    private TransactionService transactionService;
    private UserService userService;
    private ProductService productService;
    private ILogger<SubscriptionService> logger;
    private PaymentContext context;

    /// <summary>
    /// Represents a service for managing subscriptions.
    /// </summary>
    /// <param name="logger">The logger instance for logging.</param>
    /// <param name="transactionService">The transaction service for managing transactions.</param>
    /// <param name="userService">The user service for managing users.</param>
    /// <param name="productService">The product service for managing products.</param>
    /// <param name="context"></param>
    public SubscriptionService(
        ILogger<SubscriptionService> logger,
        TransactionService transactionService,
        UserService userService,
        ProductService productService,
        PaymentContext context)
    {
        this.logger = logger;
        this.transactionService = transactionService;
        this.userService = userService;
        this.productService = productService;
        this.context = context;
    }

    internal async Task PaymentReceived(Webhook data)
    {
        var customData = data.Meta.CustomData;
        var product = context.TopUpProducts.Find(customData.ProductId);
        logger.LogInformation($"Payment received for user {customData.UserId} for product {customData.ProductId}, crediting");
        await transactionService.AddTopUp(customData.ProductId, customData.UserId, data.Data.Id + "-topup");
        logger.LogInformation("starting purchase");
        await transactionService.PurchaseService(product.Slug, customData.UserId, 1, data.Data.Id, product);
        logger.LogInformation($"Payment received for user {customData.UserId} for product {customData.ProductId} extended by {product.OwnershipSeconds}");
    }

    internal async Task RefundPayment(Webhook webhook)
    {
        var userId = webhook.Meta.CustomData.UserId;
        var reference = webhook.Data.Id;
        await RevertPurchase(userId, reference);
        await RevertPurchase(userId, reference + "-topup");
    }

    private async Task RevertPurchase(string userId, string reference)
    {
        var transactionId = context.FiniteTransactions.Where(t => t.Reference == reference).Select(t => t.Id).FirstOrDefault();
        await transactionService.RevertPurchase(userId, transactionId);
    }
}
