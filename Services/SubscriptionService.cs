using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Models.LemonSqueezy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Coflnet.Payments.Services;

public class SubscriptionService
{
    private TransactionService transactionService;
    private UserService userService;
    private ProductService productService;
    private ILogger<SubscriptionService> logger;
    private PaymentContext context;
    private LemonSqueezyService lemonSqueezyService;

    /// <summary>
    /// Represents a service for managing subscriptions.
    /// </summary>
    /// <param name="logger">The logger instance for logging.</param>
    /// <param name="transactionService">The transaction service for managing transactions.</param>
    /// <param name="userService">The user service for managing users.</param>
    /// <param name="productService">The product service for managing products.</param>
    /// <param name="context"></param>
    /// <param name="lemonSqueezyService"></param>
    public SubscriptionService(
        ILogger<SubscriptionService> logger,
        TransactionService transactionService,
        UserService userService,
        ProductService productService,
        PaymentContext context,
        LemonSqueezyService lemonSqueezyService)
    {
        this.logger = logger;
        this.transactionService = transactionService;
        this.userService = userService;
        this.productService = productService;
        this.context = context;
        this.lemonSqueezyService = lemonSqueezyService;
    }

    internal async Task<IEnumerable<UserSubscription>> GetUserSubscriptions(string userId)
    {
        return await context.Subscriptions.Where(s => s.User == context.Users.Where(u => u.ExternalId == userId).FirstOrDefault()).ToListAsync();
    }

    public async Task UpdateSubscription(Webhook webhook)
    {
        var userId = webhook.Meta.CustomData.UserId;
        var product = await context.TopUpProducts.FindAsync(webhook.Meta.CustomData.ProductId);
        var subscription = await context.Subscriptions.Where(s => s.User.ExternalId == userId && s.Product == product).FirstOrDefaultAsync();
        if (subscription == null)
        {
            subscription = new UserSubscription()
            {
                User = await userService.GetOrCreate(userId),
                Product = product
            };
            context.Subscriptions.Add(subscription);
        }
        else
        {
            context.Update(subscription);
        }
        var attributes = webhook.Data.Attributes;
        subscription.RenewsAt = attributes.RenewsAt.Value;
        subscription.UpdatedAt = attributes.UpdatedAt;
        subscription.Status = attributes.Status;
        subscription.CreatedAt = attributes.CreatedAt;
        subscription.EndsAt = attributes.EndsAt;
        subscription.ExternalCustomerId = attributes.CustomerId.ToString();
        subscription.ExternalId = webhook.Data.Id;
        await context.SaveChangesAsync();
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
        try
        {
            var subscriptionId = data.Data.Attributes.SubscriptionId.ToString();
            var subscription = await context.Subscriptions.Where(s => s.ExternalId == subscriptionId).FirstOrDefaultAsync();
            if (subscription != null)
            {
                subscription.PaymentAmount = data.Data.Attributes.TotalFormatted;
                await context.SaveChangesAsync();
            }
        }
        catch (System.Exception e)
        {
            logger.LogError(e, "Error updating subscription with amount");
        }
    }

    public async Task CancelSubscription(string userId, string subscriptionId)
    {
        var subscription = await context.Subscriptions.Where(s => s.User.ExternalId == userId && s.ExternalId == subscriptionId).FirstOrDefaultAsync();
        if (subscription == null)
        {
            return;
        }
        await lemonSqueezyService.CancelSubscription(subscription.ExternalId);
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
