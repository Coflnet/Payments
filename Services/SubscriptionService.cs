using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Models.LemonSqueezy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Coflnet.Payments.Services;

public partial class SubscriptionService
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
            .Where(s => s.User.ExternalId == userId)
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

    public Task UpdateSubscription(Webhook webhook) => WithSubscriptionLock(webhook.Data.Id, async () =>
    {
        await UpdateSubscriptionLocked(webhook);
        return true;
    });

    private async Task UpdateSubscriptionLocked(Webhook webhook)
    {
        if (webhook?.Data?.Attributes == null)
        {
            logger.LogWarning("Ignoring malformed Lemon Squeezy subscription webhook without data attributes");
            return;
        }

        var customData = webhook.Meta?.CustomData;
        var userId = customData?.UserId;
        var subscription = await context.Subscriptions
            .Include(s => s.User).Include(s => s.Product).ThenInclude(p => p.Groups)
            .Where(s => s.ExternalId == webhook.Data.Id)
            .OrderByDescending(s => s.UpdatedAt).FirstOrDefaultAsync();
        TopUpProduct product = null;
        if (subscription != null)
        {
            if (userId != null && userId != subscription.User.ExternalId)
                throw new ApiException("Subscription owner does not match the webhook.");
            userId = subscription.User.ExternalId;
            if (webhook.Data.Attributes.VariantId > 0 && lemonSqueezyService.SubscriptionVariants.Count > 0)
            {
                var remote = await lemonSqueezyService.GetSubscription(subscription.ExternalId);
                await SynchronizePlan(subscription, remote);
                webhook = new Webhook(webhook.Meta, remote);
            }
            product = await context.TopUpProducts.FindAsync(subscription.Product.Id);
        }
        else if (!string.IsNullOrWhiteSpace(userId) && customData.ProductId != 0)
            product = await context.TopUpProducts.FindAsync(customData.ProductId);

        if (string.IsNullOrWhiteSpace(userId) || product == null)
        {
            logger.LogWarning(
                "Unable to resolve user/product for Lemon Squeezy subscription {SubscriptionId}; ignoring {EventName}",
                webhook.Data.Id,
                webhook.Meta?.EventName);
            return;
        }

        if (subscription == null)
        {
            subscription = new UserSubscription()
            {
                User = await userService.GetOrCreate(userId),
                Product = product
            };
            context.Subscriptions.Add(subscription);
        }
        var attributes = webhook.Data.Attributes;
        if (subscription.ProviderVariantId == null && attributes.VariantId > 0)
            subscription.ProviderVariantId = attributes.VariantId;
        if (attributes.RenewsAt.HasValue)
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
            await HandleTrialSubscription(webhook, subscription, product, userId);
        }
        // Handle PayPal subscriptions: PayPal doesn't send subscription_payment_success webhooks when order is created
        // so we need to treat subscription_created with PayPal payment processor as a payment event
        else if (customData != null
            && webhook.Meta?.EventName == "subscription_created" 
            && attributes.Status == "active" 
            && attributes.PaymentProcessor?.Equals("paypal", StringComparison.OrdinalIgnoreCase) == true)
        {
            logger.LogInformation("PayPal subscription created for user {UserId} product {ProductId}, treating as payment", 
                userId, product.Id);
            await TryExtendSubscription(webhook, new CustomData(userId, product.Id, decimal.ToInt64(product.Cost), "True"));
        }
        
        await context.SaveChangesAsync();

    }

    /// <summary>
    /// Handle trial subscription - grant access for trial period without crediting coins
    /// </summary>
    private async Task HandleTrialSubscription(Webhook webhook, UserSubscription subscription, TopUpProduct product, string userId)
    {
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

    internal Task<CustomData> PaymentReceived(Webhook data) =>
        WithSubscriptionLock(data.Data.Attributes.SubscriptionId.ToString(), () => PaymentReceivedLocked(data));

    private async Task<CustomData> PaymentReceivedLocked(Webhook data)
    {
        var effectiveCustomData = await ResolvePaymentCustomData(data);
        if (await context.RefundedSubscriptionInvoices.AnyAsync(i => i.InvoiceId == data.Data.Id))
            return new CustomData(effectiveCustomData.UserId, effectiveCustomData.ProductId, 0,
                effectiveCustomData.IsSubscription, effectiveCustomData.CreatorCode);

        // For PayPal subscriptions with billing_reason "initial", check if we've already credited
        // via subscription_created (PayPal doesn't always send subscription_payment_success reliably,
        // but when it does, we shouldn't double-credit)
        var billingReason = data.Data.Attributes.BillingReason;
        if (billingReason?.Equals("initial", StringComparison.OrdinalIgnoreCase) == true)
        {
            var subscriptionId = data.Data.Attributes.SubscriptionId.ToString();
            var subscription = await context.Subscriptions.Where(s => s.ExternalId == subscriptionId).FirstOrDefaultAsync();
            
            // Check if this is a PayPal subscription that was already credited via subscription_created
            if (subscription != null)
            {
                // Look for existing transaction from subscription_created using the subscription's created_at date
                var createdDate = subscription.CreatedAt.Date.ToString("yyyy-MM-dd");
                var existingReference = subscriptionId + createdDate;
                var existingTransaction = await context.FiniteTransactions
                    .Where(t => t.Reference == existingReference || t.Reference == existingReference + "-topup")
                    .FirstOrDefaultAsync();
                
                if (existingTransaction != null)
                {
                    logger.LogInformation("Initial payment for subscription {SubscriptionId} was already credited via subscription_created, skipping duplicate from subscription_payment_success", 
                        subscriptionId);
                    return effectiveCustomData;
                }
            }
        }
        
        if (!string.Equals(billingReason, "updated", StringComparison.OrdinalIgnoreCase))
            await TryExtendSubscription(data, effectiveCustomData);
        else
        {
            // Prorations are separate invoices, not another full service purchase.
            // Keep a zero-coin receipt so duplicate paid/recovered events are idempotent.
            await transactionService.WithTransactionAsync(async (_, _) =>
            {
                var marker = await productService.GetProduct("revert");
                await transactionService.CreateTransaction(marker, await userService.GetOrCreate(effectiveCustomData.UserId),
                    0, "ls-invoice-" + data.Data.Id);
            });
            effectiveCustomData = new CustomData(effectiveCustomData.UserId, effectiveCustomData.ProductId,
                0, effectiveCustomData.IsSubscription, effectiveCustomData.CreatorCode);
        }
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
        return effectiveCustomData;
    }

    private async Task<CustomData> ResolvePaymentCustomData(Webhook data)
    {
        var customData = data.Meta?.CustomData;
        var subscriptionId = data.Data.Attributes.SubscriptionId.ToString();
        var subscription = await context.Subscriptions
            .Include(s => s.User).Include(s => s.Product).ThenInclude(p => p.Groups)
            .Where(s => s.ExternalId == subscriptionId)
            .OrderByDescending(s => s.UpdatedAt).FirstOrDefaultAsync();
        if (subscription == null && !string.IsNullOrWhiteSpace(customData?.UserId) && customData.ProductId != 0)
            return customData;
        if (subscription != null && !string.IsNullOrWhiteSpace(customData?.UserId)
            && customData.UserId != subscription.User.ExternalId)
            throw new ApiException("Subscription owner does not match the invoice.");
        if (subscription != null && lemonSqueezyService.SubscriptionVariants.Count > 0)
        {
            var remote = await lemonSqueezyService.GetSubscription(subscriptionId);
            if (data.Data.Attributes.BillingReason == "renewal" && remote.Attributes.Status == "active")
            {
                var slug = lemonSqueezyService.SubscriptionVariants.FirstOrDefault(p => p.Value == remote.Attributes.VariantId).Key;
                var target = slug == null ? null : await context.TopUpProducts.Include(p => p.Groups).SingleOrDefaultAsync(p => p.Slug == slug);
                if (target != null && CanChangePlan(subscription.Product, target))
                {
                    await PrepareOwnership(subscription);
                    if (!await ApplyPlan(subscription, target, remote, null))
                        throw new ApiException("The provider subscription price is still updating. Please retry.");
                }
            }
            await SynchronizePlan(subscription, remote);
            await context.SaveChangesAsync();
        }
        var product = subscription?.Product == null
            ? null
            : await context.TopUpProducts.FindAsync(subscription.Product.Id);
        if (string.IsNullOrWhiteSpace(subscription?.User?.ExternalId) || product == null)
        {
            throw new InvalidOperationException(
                $"Cannot process Lemon Squeezy invoice {data.Data.Id}: subscription {subscriptionId} was not found with a user and top-up product");
        }

        logger.LogInformation(
            "Resolved missing Lemon Squeezy custom_data for subscription {SubscriptionId} to user {UserId} and product {ProductId}",
            subscriptionId, subscription.User.ExternalId, product.Id);
        return new CustomData(subscription.User.ExternalId, product.Id, decimal.ToInt64(product.Cost), "True");
    }

    /// <summary>
    /// Usually a `subscription_payment_success` webhook is received, sometimes it isn't.
    /// To make sure the customer still receives his product this also gets called with subscription_updated and subscription_created
    /// </summary>
    /// <param name="data"></param>
    /// <returns></returns>
    private async Task TryExtendSubscription(Webhook data, CustomData effectiveCustomData)
    {
        var product = context.TopUpProducts.Find(effectiveCustomData.ProductId);
        var subscriptionId = data.Data.Type == "subscription-invoices"
            ? data.Data.Attributes.SubscriptionId.ToString() : data.Data.Id;
        var referenceId = data.Data.Id + data.Data.Attributes.CreatedAt.Date.ToString("yyyy-MM-dd");
        
        // Skip extension for trial subscriptions - they don't pay yet
        // Trial access is handled separately in HandleTrialSubscription
        if (data.Data.Attributes.Status == "on_trial")
        {
            logger.LogInformation("Subscription is on trial for user {UserId} product {ProductId}, skipping coin credit", 
                effectiveCustomData.UserId, effectiveCustomData.ProductId);
            return;
        }
        
        if (data.Data.Type != "subscription-invoices" && await context.RefundedSubscriptionInvoices
            .AnyAsync(i => i.SubscriptionId == subscriptionId && i.BillingReason == "initial"))
            return;
        if (data.Data.Type == "subscription-invoices")
        {
            // Skip coin credit/service extension for 0$ initial trial invoices, as trial access is already handled by HandleTrialSubscription
            if (data.Data.Attributes.Total == 0 && data.Data.Attributes.Subtotal == 0 && data.Data.Attributes.BillingReason == "initial")
            {
                logger.LogInformation("Subscription invoice is for 0 amount (initial trial), skipping coin credit for user {UserId} product {ProductId}", 
                    effectiveCustomData.UserId, effectiveCustomData.ProductId);
                return;
            }

            // Respect historical date-based references when an old invoice is redelivered after rollout.
            var legacy = subscriptionId + data.Data.Attributes.CreatedAt.Date.ToString("yyyy-MM-dd");
            if (await context.FiniteTransactions.AnyAsync(t => t.User.ExternalId == effectiveCustomData.UserId
                && (t.Reference == legacy || t.Reference == legacy + "-topup")))
                return;
            referenceId = "ls-invoice-" + data.Data.Id;
            if (await context.FiniteTransactions.AnyAsync(t => t.User.ExternalId == effectiveCustomData.UserId
                && (t.Reference == referenceId || t.Reference == referenceId + "-topup")))
                throw new TransactionService.DupplicateTransactionException();
            logger.LogInformation($"Payment received for user {effectiveCustomData.UserId} for product {effectiveCustomData.ProductId}, crediting");
        }
        else
        {
            if (await context.FiniteTransactions.AnyAsync(t => t.User.ExternalId == effectiveCustomData.UserId
                && (t.Reference == referenceId || t.Reference == referenceId + "-topup")))
                return;
            // is subscription update, check current expiry and abbort if its more than 1 day in the future already
            var expires = product.SlotCount > 0
                ? await context.TierSlots.Where(s => s.User.ExternalId == effectiveCustomData.UserId && s.SubscriptionId == subscriptionId)
                    .Select(s => (DateTime?)s.Expires).MinAsync()
                : await context.OwnerShips.Where(s => s.User.ExternalId == effectiveCustomData.UserId && s.Product.Id == effectiveCustomData.ProductId)
                    .Select(s => (DateTime?)s.Expires).FirstOrDefaultAsync();
            if (expires > data.Data.Attributes.RenewsAt?.AddDays(-2))
            {
                logger.LogInformation("Subscription already extended, skipping");
                return;
            }
            if (data.Data.Attributes.RenewsAt < DateTime.UtcNow)
            {// there sometime is an extra webhook if the initial payment attempt didn't go through, we ignore that
                logger.LogInformation("Subscription renew in the past, skipping ({renewTime})", data.Data.Attributes.RenewsAt);
                return;
            }
            logger.LogInformation($"Subscription extended for user {effectiveCustomData.UserId} for product {effectiveCustomData.ProductId}, crediting");
        }

        await using var transaction = await transactionService.StartDbTransaction();
        try
        {
            await transactionService.AddTopUp(effectiveCustomData.ProductId, effectiveCustomData.UserId, referenceId + "-topup");
            logger.LogInformation("starting purchase");
            await transactionService.PurchaseService(product.Slug, effectiveCustomData.UserId, 1, referenceId, product,
                subscriptionId: subscriptionId);
            await transaction.CommitAsync();
            logger.LogInformation($"Payment received for user {effectiveCustomData.UserId} for product {effectiveCustomData.ProductId} extended by {product.OwnershipSeconds}");
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error processing topup/purchase");
            await transaction.RollbackAsync();
            throw;
        }
    }

    public Task CancelSubscription(string userId, string subscriptionId) => WithSubscriptionLock(subscriptionId, async () =>
    {
        await CancelSubscriptionLocked(userId, subscriptionId);
        return true;
    });

    private async Task CancelSubscriptionLocked(string userId, string subscriptionId)
    {
        var subscription = await context.Subscriptions.Where(s => s.User.ExternalId == userId && s.ExternalId == subscriptionId).FirstOrDefaultAsync();
        if (subscription == null)
        {
            throw new ApiException("Subscription not found");
        }
        var remote = await lemonSqueezyService.CancelSubscription(subscription.ExternalId);
        if (remote.Attributes.Status != "cancelled" || remote.Attributes.EndsAt == null)
            throw new ApiException("Lemon Squeezy did not confirm the cancellation.");
        subscription.Status = remote.Attributes.Status;
        subscription.EndsAt = remote.Attributes.EndsAt;
        subscription.UpdatedAt = remote.Attributes.UpdatedAt;
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Resume a cancelled subscription that is still in grace period.
    /// </summary>
    /// <param name="userId">The user ID</param>
    /// <param name="subscriptionId">The external subscription ID</param>
    /// <returns>True if successfully resumed</returns>
    public Task<bool> ResumeSubscription(string userId, string subscriptionId) =>
        WithSubscriptionLock(subscriptionId, () => ResumeSubscriptionLocked(userId, subscriptionId));

    private async Task<bool> ResumeSubscriptionLocked(string userId, string subscriptionId)
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
        
        var remote = await lemonSqueezyService.ResumeSubscription(subscription.ExternalId);
        if (remote.Attributes.Status != "active" || remote.Attributes.EndsAt != null)
            throw new ApiException("Lemon Squeezy did not confirm reactivation.");
        subscription.Status = remote.Attributes.Status;
        subscription.EndsAt = null;
        subscription.UpdatedAt = remote.Attributes.UpdatedAt;
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
    public Task<RefundResponse> RefundSubscriptionPayment(string userId, string subscriptionId, string invoiceId, RefundRequest request) =>
        WithSubscriptionLock(subscriptionId, () => RefundSubscriptionPaymentLocked(userId, subscriptionId, invoiceId, request));

    private async Task<RefundResponse> RefundSubscriptionPaymentLocked(string userId, string subscriptionId, string invoiceId, RefundRequest request)
    {
        // Validate user subscription
        var subscription = await OwnedSubscription(userId, subscriptionId)
            ?? throw new ApiException("Subscription not found");

        // Get the invoice details to check the age
        var invoices = await lemonSqueezyService.GetSubscriptionInvoicesAsync(subscriptionId);
        var invoice = invoices?.FirstOrDefault(i => i.Id == invoiceId);
        
        if (invoice == null || invoice.SubscriptionId.ToString() != subscriptionId)
            throw new ApiException("Invoice not found");
        if (invoice.Refunded || invoice.Status == "refunded")
        {
            await ApplyInvoiceRefund(subscription, invoice.Id, invoice.BillingReason, invoice.CreatedAt, true);
            return new RefundResponse { Id = invoice.Id, Refunded = true, RefundedAmount = invoice.RefundedAmount, Status = invoice.Status };
        }
        if (invoice.Status is not ("paid" or "partial_refund") || invoice.Total <= 0
            || request?.Amount <= 0 || request?.Amount > invoice.Total - invoice.RefundedAmount)
            throw new ApiException("Invalid invoice refund amount or status.");
        
        // Check if invoice is within the 3-day refund window
        var daysSinceCreation = (DateTime.UtcNow - invoice.CreatedAt).TotalDays;
        if (daysSinceCreation > 3)
        {
            throw new ApiException($"Refund window has expired. Invoices can only be refunded within 3 days of creation. This invoice was created {daysSinceCreation:F1} days ago.");
        }
        
        if (invoice.BillingReason == "updated")
            await SynchronizePlan(subscription, await lemonSqueezyService.GetSubscription(subscriptionId));
        // Issue the refund
        var refundResponse = await lemonSqueezyService.RefundInvoiceAsync(invoiceId, request?.Amount);
        
        if (refundResponse == null)
        {
            throw new ApiException("Failed to process refund. Please try again later.");
        }
        
        await ApplyInvoiceRefund(subscription, invoice.Id, invoice.BillingReason, invoice.CreatedAt,
            refundResponse.Refunded || refundResponse.Status == "refunded" || refundResponse.RefundedAmount >= invoice.Total);
        logger.LogInformation("Subscription payment refunded for user {UserId}, subscription {SubscriptionId}, invoice {InvoiceId}: amount={Amount}", 
            userId, subscriptionId, invoiceId, refundResponse.RefundedAmount);
        
        return refundResponse;
    }

    internal Task RefundPayment(Webhook webhook) => WithSubscriptionLock(webhook.Data.Attributes.SubscriptionId.ToString(), async () =>
    {
        var attrs = webhook.Data.Attributes;
        var subscription = await context.Subscriptions.Include(s => s.User).Include(s => s.Product).ThenInclude(p => p.Groups)
            .FirstOrDefaultAsync(s => s.ExternalId == attrs.SubscriptionId.ToString());
        if (subscription == null)
        {
            var custom = webhook.Meta?.CustomData;
            if (string.IsNullOrWhiteSpace(custom?.UserId))
                throw new ApiException("Subscription refund owner could not be resolved.");
            subscription = new UserSubscription { User = await userService.GetOrCreate(custom.UserId),
                Product = await context.TopUpProducts.FindAsync(custom.ProductId), ExternalId = attrs.SubscriptionId.ToString() };
        }
        await ApplyInvoiceRefund(subscription, webhook.Data.Id, attrs.BillingReason, attrs.CreatedAt,
            attrs.Refunded || attrs.Status == "refunded" || (attrs.Total > 0 && attrs.RefundedAmount >= attrs.Total));
        return true;
    });

    private async Task ApplyInvoiceRefund(UserSubscription subscription, string invoiceId, string reason, DateTime createdAt, bool fullRefund)
    {
        // Partial refunds are price adjustments; they do not revoke the paid service.
        if (!fullRefund || await context.RefundedSubscriptionInvoices.AnyAsync(i => i.InvoiceId == invoiceId))
            return;
        await transactionService.WithTransactionAsync(async (_, _) =>
        {
            context.RefundedSubscriptionInvoices.Add(new RefundedSubscriptionInvoice
                { InvoiceId = invoiceId, SubscriptionId = subscription.ExternalId, BillingReason = reason });
            if (reason == "updated")
            {
                var change = await context.SubscriptionPlanChanges.Include(c => c.PreviousProduct).Include(c => c.Product)
                    .SingleOrDefaultAsync(c => c.InvoiceId == invoiceId && c.SubscriptionId == subscription.ExternalId);
                if (change != null)
                {
                    change.Refunded = true;
                    var changes = await context.SubscriptionPlanChanges.Include(c => c.PreviousProduct).Include(c => c.Product)
                        .Where(c => c.SubscriptionId == subscription.ExternalId && c.PeriodEnd == change.PeriodEnd)
                        .OrderBy(c => c.ChangedAt).ThenBy(c => c.Id).ToListAsync();
                    var expiry = await AccessExpiry(subscription);
                    // A refund for an earlier period must not undo a later paid renewal.
                    if (expiry <= change.PeriodEnd.AddSeconds(5))
                        await SetAccessProduct(subscription, changes.LastOrDefault(c => !c.Refunded)?.Product ?? changes[0].PreviousProduct);
                }
            }
            else
            {
                var reference = "ls-invoice-" + invoiceId;
                if (!await context.FiniteTransactions.AnyAsync(t => t.User.Id == subscription.User.Id && t.Reference == reference))
                    reference = subscription.ExternalId + createdAt.Date.ToString("yyyy-MM-dd");
                await RevertPurchase(subscription.User.ExternalId, reference);
                await RevertPurchase(subscription.User.ExternalId, reference + "-topup", adjustTime: false);
            }
            await context.SaveChangesAsync();
        });
    }

    private async Task RevertPurchase(string userId, string reference, bool adjustTime = true)
    {
        var transactionId = context.FiniteTransactions.Where(t => t.Reference == reference && t.User.ExternalId == userId).Select(t => t.Id).FirstOrDefault();
        if (transactionId == 0)
        {
            logger.LogWarning("No transaction found for reference {Reference} for user {UserId}, skipping revert", reference, userId);
            return;
        }
        await transactionService.RevertPurchase(userId, transactionId, adjustTime);
    }
}
