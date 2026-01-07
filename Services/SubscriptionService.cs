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
        subscription.TrialEndsAt = attributes.TrialEndsAt;
        
        // Handle trial subscription: grant access for trial period but don't credit coins
        if (attributes.Status == "on_trial" && attributes.TrialEndsAt.HasValue)
        {
            await HandleTrialSubscription(webhook, subscription, product);
        }
        
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

    /// <summary>
    /// Handle trial subscription - grant access for trial period without crediting coins
    /// </summary>
    private async Task HandleTrialSubscription(Webhook webhook, UserSubscription subscription, TopUpProduct product)
    {
        var userId = webhook.Meta.CustomData.UserId;
        var attributes = webhook.Data.Attributes;
        var trialEndDate = attributes.TrialEndsAt.Value;
        
        // Record trial usage to prevent multiple trials
        await lemonSqueezyService.RecordTrialUsageAsync(userId, product.Id, webhook.Data.Id);
        subscription.TrialUsedAt = DateTime.UtcNow;
        
        // Check if we've already granted trial access for this subscription
        var trialReferenceId = $"trial-{webhook.Data.Id}";
        var existingOwnership = await context.OwnerShips
            .Where(o => o.User.ExternalId == userId && o.Product.Slug == product.Slug)
            .FirstOrDefaultAsync();

        if (existingOwnership != null)
        {
            // Check if already extended by this trial
            var trialTransaction = await context.FiniteTransactions
                .Where(t => t.Reference == trialReferenceId)
                .FirstOrDefaultAsync();
            
            if (trialTransaction != null)
            {
                logger.LogInformation("Trial access already granted for user {UserId} product {ProductId}", userId, product.Id);
                return;
            }
            
            // Extend existing ownership to trial end date if trial extends beyond current expiry
            if (existingOwnership.Expires < trialEndDate)
            {
                existingOwnership.Expires = trialEndDate;
                logger.LogInformation("Extended existing ownership for user {UserId} product {ProductId} to trial end {TrialEnd}", 
                    userId, product.Id, trialEndDate);
            }
        }
        else
        {
            // Create new ownership for trial period
            var user = await userService.GetOrCreate(userId);
            // Look for product by slug in both Products and TopUpProducts (with SERVICE type)
            Product serviceProduct = await context.Products.Where(p => p.Slug == product.Slug).FirstOrDefaultAsync();
            if (serviceProduct == null)
            {
                // TopUpProduct with SERVICE type can also be used for ownership
                serviceProduct = await context.TopUpProducts.Where(p => p.Slug == product.Slug && p.Type.HasFlag(Product.ProductType.SERVICE)).FirstOrDefaultAsync();
            }
            if (serviceProduct == null)
            {
                logger.LogWarning("Could not find service product for trial slug {ProductSlug}", product.Slug);
                return;
            }
            
            var ownership = new OwnerShip
            {
                User = user,
                Product = serviceProduct,
                Expires = trialEndDate
            };
            context.OwnerShips.Add(ownership);
            logger.LogInformation("Created trial ownership for user {UserId} product {ProductSlug} until {TrialEnd}", 
                userId, product.Slug, trialEndDate);
        }

        // Record a $0 transaction for audit trail (no coins credited)
        try
        {
            var revertProduct = await productService.GetProduct("revert");
            var user = await userService.GetOrCreate(userId);
            var transaction = new FiniteTransaction
            {
                User = user,
                Product = revertProduct,
                Amount = 0,
                Reference = trialReferenceId,
                Timestamp = DateTime.UtcNow
            };
            context.FiniteTransactions.Add(transaction);
            logger.LogInformation("Recorded trial transaction for user {UserId} product {ProductId}", userId, product.Id);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not record trial transaction, continuing");
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
        
        // Skip extension for trial subscriptions - they don't pay yet
        // Trial access is handled separately in HandleTrialSubscription
        if (data.Data.Attributes.Status == "on_trial")
        {
            logger.LogInformation("Subscription is on trial for user {UserId} product {ProductId}, skipping coin credit", 
                customData.UserId, customData.ProductId);
            return;
        }
        
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

        await using var transaction = await transactionService.StartDbTransaction();
        try
        {
            await transactionService.AddTopUp(customData.ProductId, customData.UserId, referenceId + "-topup");
            logger.LogInformation("starting purchase");
            await transactionService.PurchaseService(product.Slug, customData.UserId, 1, referenceId, product);
            await transaction.CommitAsync();
            logger.LogInformation($"Payment received for user {customData.UserId} for product {customData.ProductId} extended by {product.OwnershipSeconds}");
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error processing topup/purchase");
            await transaction.RollbackAsync();
            throw;
        }
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

    /// <summary>
    /// Resume a cancelled subscription that is still in grace period.
    /// </summary>
    /// <param name="userId">The user ID</param>
    /// <param name="subscriptionId">The external subscription ID</param>
    /// <returns>True if successfully resumed</returns>
    public async Task<bool> ResumeSubscription(string userId, string subscriptionId)
    {
        var subscription = await context.Subscriptions
            .Where(s => s.User.ExternalId == userId && s.ExternalId == subscriptionId)
            .FirstOrDefaultAsync();
        
        if (subscription == null)
        {
            throw new ApiException("Subscription not found");
        }
        
        if (subscription.Status != "cancelled")
        {
            throw new ApiException($"Subscription is not cancelled, current status: {subscription.Status}");
        }
        
        if (subscription.EndsAt.HasValue && subscription.EndsAt.Value < DateTime.UtcNow)
        {
            throw new ApiException("Subscription grace period has expired and cannot be resumed");
        }
        
        await lemonSqueezyService.ResumeSubscription(subscription.ExternalId);
        
        subscription.Status = "active";
        await context.SaveChangesAsync();
        
        return true;
    }

    /// <summary>
    /// Get all invoices for a subscription
    /// </summary>
    /// <param name="userId">The user ID</param>
    /// <param name="subscriptionId">The external subscription ID</param>
    /// <returns>List of subscription invoices</returns>
    public async Task<IEnumerable<SubscriptionInvoice>> GetSubscriptionInvoices(string userId, string subscriptionId)
    {
        var subscription = await context.Subscriptions
            .Where(s => s.User.ExternalId == userId && s.ExternalId == subscriptionId)
            .FirstOrDefaultAsync();
        
        if (subscription == null)
        {
            throw new ApiException("Subscription not found");
        }
        
        return await lemonSqueezyService.GetSubscriptionInvoicesAsync(subscription.ExternalId);
    }

    /// <summary>
    /// Generate a download link for a subscription invoice
    /// </summary>
    /// <param name="userId">The user ID</param>
    /// <param name="invoiceId">The invoice ID</param>
    /// <param name="request">Invoice generation request with address details</param>
    /// <returns>Download URL response</returns>
    public async Task<InvoiceDownloadResponse> GenerateInvoiceDownloadLink(string userId, string invoiceId, GenerateInvoiceRequest request)
    {
        // Validate required fields
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new ApiException("Name is required");
        if (string.IsNullOrWhiteSpace(request.Address))
            throw new ApiException("Address is required");
        if (string.IsNullOrWhiteSpace(request.City))
            throw new ApiException("City is required");
        if (string.IsNullOrWhiteSpace(request.ZipCode))
            throw new ApiException("ZIP code is required");
        if (string.IsNullOrWhiteSpace(request.Country))
            throw new ApiException("Country is required");
        
        // For US and CA, state is required
        if ((request.Country == "US" || request.Country == "CA") && string.IsNullOrWhiteSpace(request.State))
            throw new ApiException("State is required for US and CA");
        
        var downloadUrl = await lemonSqueezyService.GenerateInvoiceDownloadLinkAsync(invoiceId, request);
        
        if (downloadUrl == null)
        {
            throw new ApiException("Failed to generate invoice download link");
        }
        
        return new InvoiceDownloadResponse { DownloadUrl = downloadUrl };
    }

    /// <summary>
    /// Refund a subscription invoice payment. Refunds can only be issued up to 3 days after the invoice was created.
    /// </summary>
    /// <param name="userId">The user ID</param>
    /// <param name="subscriptionId">The external subscription ID from LemonSqueezy</param>
    /// <param name="invoiceId">The subscription invoice ID</param>
    /// <param name="request">Optional refund amount in cents. If not specified, a full refund will be issued.</param>
    /// <returns>RefundResponse containing the updated invoice details</returns>
    public async Task<RefundResponse> RefundSubscriptionPayment(string userId, string subscriptionId, string invoiceId, RefundRequest request)
    {
        // Validate user subscription
        var subscription = await context.Subscriptions
            .Where(s => s.User.ExternalId == userId && s.ExternalId == subscriptionId)
            .FirstOrDefaultAsync();
        
        if (subscription == null)
        {
            throw new ApiException("Subscription not found");
        }
        
        // Get the invoice details to check the age
        var invoices = await lemonSqueezyService.GetSubscriptionInvoicesAsync(subscriptionId);
        var invoice = invoices?.FirstOrDefault(i => i.Id == invoiceId);
        
        if (invoice == null)
        {
            throw new ApiException("Invoice not found");
        }
        
        // Check if invoice is within the 3-day refund window
        var daysSinceCreation = (DateTime.UtcNow - invoice.CreatedAt).TotalDays;
        if (daysSinceCreation > 3)
        {
            throw new ApiException($"Refund window has expired. Invoices can only be refunded within 3 days of creation. This invoice was created {daysSinceCreation:F1} days ago.");
        }
        
        // Issue the refund
        var refundResponse = await lemonSqueezyService.RefundInvoiceAsync(invoiceId, request?.Amount);
        
        if (refundResponse == null)
        {
            throw new ApiException("Failed to process refund. Please try again later.");
        }
        
        logger.LogInformation("Subscription payment refunded for user {UserId}, subscription {SubscriptionId}, invoice {InvoiceId}: amount={Amount}", 
            userId, subscriptionId, invoiceId, refundResponse.RefundedAmount);
        
        return refundResponse;
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
