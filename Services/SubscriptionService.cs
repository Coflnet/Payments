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
        var all = await context.Subscriptions
            .Where(s => s.User == context.Users.Where(u => u.ExternalId == userId).FirstOrDefault())
            .Include(s => s.Product).ToListAsync();
        var dupplicate = all.GroupBy(s => s.ExternalId).Where(s => s.Count() > 1).FirstOrDefault()?.OrderByDescending(f => f.UpdatedAt).Skip(1).FirstOrDefault();
        if( dupplicate != null)
        {
            logger.LogWarning("Found duplicate subscription {subscriptionId} for user {userId}, removing", dupplicate.ExternalId, userId);
            context.Subscriptions.Remove(dupplicate);
            await context.SaveChangesAsync();
        }
        return all.OrderByDescending(s => s.UpdatedAt);
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
        if(subscription.Status == "expired")
        {
            var referenceId = webhook.Data.Id + webhook.Data.Attributes.UpdatedAt.Date.ToString("yyyy-MM-dd");
            logger.LogInformation("Subscription expired, reverting purchase {referenceId}", referenceId);
            await RevertPurchase(userId, referenceId + "-topup");
            await RevertPurchase(userId, referenceId);
            return;
        }
    }

    internal async Task PaymentReceived(Webhook data)
    {
        await TryExtendSubscription(data);
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

    /// <summary>
    /// Usually a `subscription_payment_success` webhook is received, sometimes it isn't.
    /// To make sure the customer still receives his product this also gets called with subscription_updated and subscription_created
    /// </summary>
    /// <param name="data"></param>
    /// <returns></returns>
    private async Task TryExtendSubscription(Webhook data)
    {
        var customData = data.Meta.CustomData;
        var product = context.TopUpProducts.Find(customData.ProductId);
        var referenceId = data.Data.Id + data.Data.Attributes.UpdatedAt.Date.ToString("yyyy-MM-dd");
        if (data.Data.Type == "subscription-invoices")
        {
            referenceId = data.Data.Attributes.SubscriptionId + data.Data.Attributes.UpdatedAt.Date.ToString("yyyy-MM-dd");
            logger.LogInformation($"Payment received for user {customData.UserId} for product {customData.ProductId}, crediting");
        }
        else
        {
            // is subscription update, check current expiry and abbort if its more than 1 day in the future already
            var subscription = await context.OwnerShips.Where(s => s.User.ExternalId == customData.UserId && s.Product.Id == customData.ProductId).FirstOrDefaultAsync();
            if (subscription != null && subscription.Expires > data.Data.Attributes.RenewsAt.Value.AddDays(-2))
            {
                logger.LogInformation("Subscription already extended, skipping");
                return;
            }
            if (data.Data.Attributes.RenewsAt < DateTime.UtcNow)
            {// there sometime is an extra webhook if the initial payment attempt didn't go through, we ignore that
                logger.LogInformation("Subscription renew in the past, skipping ({renewTime})", data.Data.Attributes.RenewsAt);
                return;
            }
            logger.LogInformation($"Subscription extended for user {customData.UserId} for product {customData.ProductId}, crediting");
        }
        try
        {
            await transactionService.AddTopUp(customData.ProductId, customData.UserId, referenceId + "-topup");
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error adding topup transaction");
            return;
        }
        logger.LogInformation("starting purchase");
        await transactionService.PurchaseService(product.Slug, customData.UserId, 1, referenceId, product);
        logger.LogInformation($"Payment received for user {customData.UserId} for product {customData.ProductId} extended by {product.OwnershipSeconds}");
    }

    public async Task CancelSubscription(string userId, string subscriptionId)
    {
        var subscription = await context.Subscriptions.Where(s => s.User.ExternalId == userId && s.ExternalId == subscriptionId).FirstOrDefaultAsync();
        if (subscription == null)
        {
            throw new ApiException("Subscription not found");
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
