using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Models.PayPal;
using Coflnet.Payments.Services.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Coflnet.Payments.Services;

/// <summary>
/// PayPal Subscriptions. Only subscriptions created by the checkout endpoint (recorded as a PaymentRequest)
/// are accepted, and only once PayPal reports them ACTIVE. Paid periods are granted per sale
/// (reference paypal-sale-{id}) for exactly the price this service set.
/// </summary>
public partial class SubscriptionService
{
    private const string PayPalCancelReason = "Cancelled by the customer";
    private const string ExpiredCodeCancelReason = "Approved after the discount code of the checkout expired";

    /// <summary>
    /// How long after its discount code expired a checkout started before the expiry may still be approved at the
    /// discounted price; the same as the 24 hours a Stripe checkout session stays open.
    /// </summary>
    internal static readonly TimeSpan ExpiredCodeGrace = TimeSpan.FromHours(24);

    private IPayPalSubscriptionApi PayPalApi =>
        paypal ?? throw new ApiException("PayPal subscriptions are not available.");

    private PayPalPlanCatalog PayPalPlans =>
        paypalPlans ?? throw new ApiException("PayPal subscriptions are not available.");

    /// <summary>
    /// The custom_id of a PayPal subscription created for this user and plan
    /// </summary>
    public static string PayPalCustomId(int productId, string userId) => $"{productId};{userId}";

    /// <summary>
    /// BILLING.SUBSCRIPTION.* events: synchronizes the state. On activation the first payment is granted as well
    /// if PayPal already lists it (PAYMENT.SALE.COMPLETED grants the same sale id, so this never grants twice).
    /// </summary>
    public Task<IReadOnlyList<SubscriptionPaymentOutcome>> PayPalSubscriptionChanged(string subscriptionId) =>
        WithSubscriptionLock<IReadOnlyList<SubscriptionPaymentOutcome>>(subscriptionId, async () =>
        {
            var remote = await PayPalApi.Get(subscriptionId);
            var subscription = await EnsurePayPalSubscription(remote);
            if (subscription == null)
                return [];
            await ApplyPayPalState(subscription, remote);
            await context.SaveChangesAsync();
            if (!IsPastApproval(remote.Status) || remote.BillingInfo?.LastPayment == null)
                return [];
            return await GrantListedPayPalSales(subscription, remote);
        });

    /// <summary>
    /// PAYMENT.SALE.COMPLETED of a subscription (resource.billing_agreement_id): grants the paid period once.
    /// </summary>
    public Task<SubscriptionPaymentOutcome> PayPalSaleCompleted(PayPalSale sale)
    {
        if (sale?.State != "completed" || string.IsNullOrEmpty(sale.BillingAgreementId))
            return Task.FromResult<SubscriptionPaymentOutcome>(null);
        return WithSubscriptionLock(sale.BillingAgreementId, async () =>
        {
            var remote = await PayPalApi.Get(sale.BillingAgreementId);
            if (!IsPastApproval(remote.Status))
                throw new ApiException($"PayPal subscription {remote.Id} is not active yet; retry the sale later");
            var subscription = await EnsurePayPalSubscription(remote);
            if (subscription == null)
            {
                await MarkForManualRefund(SubscriptionProviders.PayPal, sale.Id, null, ParseMoney(sale.Amount?.Total) ?? 0,
                    $"PayPal sale {sale.Id} of subscription {remote.Id}, which was not sold by this service");
                return null;
            }
            var outcome = await GrantPayPalSale(subscription, sale);
            // After the grant, so a cancelled subscription ends with the period this sale paid for.
            await ApplyPayPalState(subscription, remote);
            await context.SaveChangesAsync();
            return outcome;
        });
    }

    /// <summary>
    /// PAYMENT.SALE.REFUNDED/REVERSED: the sale state from PayPal's API decides whether the period is revoked.
    /// </summary>
    public async Task<string> PayPalSaleRefunded(string saleId)
    {
        var sale = await PayPalApi.GetSale(saleId);
        if (sale?.Id != saleId || string.IsNullOrEmpty(sale.BillingAgreementId))
            return null;
        await WithSubscriptionLock(sale.BillingAgreementId, async () =>
        {
            var subscription = await FindProviderSubscription(SubscriptionProviders.PayPal, sale.BillingAgreementId);
            if (subscription == null)
            {
                logger.LogWarning("Refund for PayPal sale {SaleId} of unknown subscription {SubscriptionId}", saleId, sale.BillingAgreementId);
                return false;
            }
            await ApplyInvoiceRefund(subscription, sale.Id, "payment", ParseTime(sale.CreateTime) ?? DateTime.UtcNow,
                sale.State is "refunded" or "reversed");
            return true;
        });
        return sale.State;
    }

    private async Task<IReadOnlyList<SubscriptionPaymentOutcome>> GrantListedPayPalSales(UserSubscription subscription, PayPalSubscription remote)
    {
        var outcomes = new List<SubscriptionPaymentOutcome>();
        try
        {
            var start = (ParseTime(remote.CreateTime) ?? subscription.CreatedAt).AddDays(-1);
            var transactions = await PayPalApi.Transactions(subscription.ExternalId, start, DateTime.UtcNow.AddDays(1));
            foreach (var transaction in transactions.Where(t => t.Status == "COMPLETED"))
            {
                var outcome = await GrantListedPayPalSale(subscription, transaction);
                if (outcome != null)
                    outcomes.Add(outcome);
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "PayPal subscription {SubscriptionId} is active but its first payment could not be checked; waiting for PAYMENT.SALE.COMPLETED",
                subscription.ExternalId);
        }
        return outcomes;
    }

    private async Task<SubscriptionPaymentOutcome> GrantListedPayPalSale(UserSubscription subscription, PayPalTransaction transaction)
    {
        var reference = SubscriptionProviders.GrantReference(SubscriptionProviders.PayPal, transaction.Id);
        if (await IsGranted(subscription.User.ExternalId, reference))
            return null;
        // The transaction list is only used when its id is provably the sale id PAYMENT.SALE.COMPLETED carries.
        var sale = await PayPalApi.GetSale(transaction.Id);
        if (sale?.Id != transaction.Id || sale.BillingAgreementId != subscription.ExternalId)
        {
            logger.LogError("PayPal transaction {TransactionId} of subscription {SubscriptionId} is not a matching sale; not granting it from the activation",
                transaction.Id, subscription.ExternalId);
            return null;
        }
        return await GrantPayPalSale(subscription, sale);
    }

    private async Task<SubscriptionPaymentOutcome> GrantPayPalSale(UserSubscription subscription, PayPalSale sale)
    {
        if (await context.RefundedSubscriptionInvoices.AnyAsync(i => i.InvoiceId == sale.Id))
            return null;
        var reference = SubscriptionProviders.GrantReference(SubscriptionProviders.PayPal, sale.Id);
        // A redelivery of a granted sale returns it again so its recording can be retried.
        if (await IsGranted(subscription.User.ExternalId, reference))
            return await PayPalOutcome(subscription, sale);
        var paid = ParseMoney(sale.Amount?.Total) ?? 0;
        if (!PayPalSaleMatchesPrice(subscription, sale))
        {
            await MarkForManualRefund(SubscriptionProviders.PayPal, sale.Id, subscription, paid,
                $"PayPal sale {sale.Id} of {subscription.ExternalId} paid {sale.Amount?.Total} {sale.Amount?.Currency}, agreed {subscription.PriceCents} cents {subscription.Currency}");
            return null;
        }
        await GrantPaidPeriod(subscription.User.ExternalId, (TopUpProduct)subscription.Product, subscription.ExternalId, reference);
        subscription.PaymentAmount = sale.Amount.Total + " " + sale.Amount.Currency;
        await SwitchToRecurringPrice(subscription);
        await context.SaveChangesAsync();
        return await PayPalOutcome(subscription, sale);
    }

    /// <summary>
    /// A "first payment only" code priced the first cycle lower: once that sale is granted, renewals are agreed at the
    /// recurring price the checkout recorded.
    /// </summary>
    private async Task SwitchToRecurringPrice(UserSubscription subscription)
    {
        var recurring = await context.PaymentRequests
            .Where(r => r.Provider == SubscriptionProviders.PayPal && r.SessionId == subscription.ExternalId)
            .Select(r => r.RecurringAmount).FirstOrDefaultAsync();
        if (recurring != null)
            subscription.PriceCents = SubscriptionPricing.ToCents(recurring.Value);
    }

    /// <summary>
    /// PayPal sales are direct sales: the paid gross contains the VAT of the checkout country, which is reported
    /// separately; the EUR amount (creator shares) is the net part.
    /// </summary>
    private async Task<SubscriptionPaymentOutcome> PayPalOutcome(UserSubscription subscription, PayPalSale sale)
    {
        var paid = ParseMoney(sale.Amount?.Total) ?? 0;
        var country = await PayPalVatCountry(subscription);
        var (net, vat, rate) = VatSplit(paid, country);
        return new SubscriptionPaymentOutcome(subscription, (TopUpProduct)subscription.Product, sale.Id, paid,
            sale.Amount?.Currency?.ToUpperInvariant(), country, null, null, null,
            ParseTime(sale.CreateTime) ?? DateTime.UtcNow, net, false, vat, rate);
    }

    /// <summary>
    /// The country whose VAT the price of a PayPal subscription contains: the verified country of its checkout, fixed
    /// for the subscription's lifetime (the user's remembered country changes with later purchases).
    /// </summary>
    private async Task<string> PayPalVatCountry(UserSubscription subscription)
    {
        var country = await context.PaymentRequests
            .Where(r => r.Provider == SubscriptionProviders.PayPal && r.SessionId == subscription.ExternalId)
            .OrderByDescending(r => r.Id).Select(r => r.Country).FirstOrDefaultAsync();
        if (country != null)
            return country;
        logger.LogWarning("PayPal subscription {SubscriptionId} has no checkout country; splitting its VAT with the user's current country",
            subscription.ExternalId);
        return subscription.User.Country;
    }

    private static bool PayPalSaleMatchesPrice(UserSubscription subscription, PayPalSale sale) =>
        subscription.PriceCents != null && ParseMoney(sale.Amount?.Total) is decimal paid
        && SubscriptionPricing.ToCents(paid) == subscription.PriceCents
        && string.Equals(sale.Amount.Currency, subscription.Currency, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Loads the local row or creates it from the PaymentRequest the checkout stored.
    /// Returns null for subscriptions this service did not create.
    /// </summary>
    private async Task<UserSubscription> EnsurePayPalSubscription(PayPalSubscription remote)
    {
        var existing = await FindProviderSubscription(SubscriptionProviders.PayPal, remote.Id);
        if (existing != null)
            return existing;
        var request = await context.PaymentRequests.Include(r => r.User).Include(r => r.ProductId)
            .Where(r => r.Provider == SubscriptionProviders.PayPal && r.SessionId == remote.Id)
            .OrderByDescending(r => r.Id).FirstOrDefaultAsync();
        var plan = await PlanBySlug(request?.ProductId?.Slug);
        var rejection = await PayPalCheckoutRejection(remote, request, plan);
        if (rejection != null)
        {
            logger.LogWarning("Ignoring PayPal subscription {SubscriptionId}: {Reason}", remote.Id, rejection);
            return null;
        }
        if (!IsPastApproval(remote.Status))
            return null; // fulfil only once the buyer approved and PayPal activated the subscription
        if (ApprovedAfterCodeExpired(request, remote))
        {
            await RejectExpiredCheckout(remote, request);
            return null;
        }
        var subscription = new UserSubscription
        {
            User = request.User,
            Product = plan,
            ProviderSlug = SubscriptionProviders.PayPal,
            ExternalId = remote.Id,
            CreatedAt = ParseTime(remote.CreateTime) ?? DateTime.UtcNow,
            PriceCents = SubscriptionPricing.ToCents(request.Amount),
            Currency = plan.CurrencyCode?.ToUpperInvariant(),
            ProviderPlanId = remote.PlanId,
            CreatorCode = request.CreatorCode,
            DiscountCode = request.DiscountCode
        };
        context.Subscriptions.Add(subscription);
        request.State = PaymentRequest.Status.PAID;
        request.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();
        return subscription;
    }

    /// <summary>
    /// A checkout priced with a discount code must be approved before the code expired plus <see cref="ExpiredCodeGrace"/>.
    /// The first payment is the best evidence of the approval time; without it, now.
    /// </summary>
    private static bool ApprovedAfterCodeExpired(PaymentRequest request, PayPalSubscription remote)
    {
        if (request.ExpiresAt is not DateTime expiry)
            return false;
        var approved = ParseTime(remote.BillingInfo?.LastPayment?.Time) ?? DateTime.UtcNow;
        return approved > expiry + ExpiredCodeGrace;
    }

    /// <summary>
    /// Cancels the subscription at PayPal; its sales are not granted and are marked for a manual refund when they arrive.
    /// </summary>
    private async Task RejectExpiredCheckout(PayPalSubscription remote, PaymentRequest request)
    {
        logger.LogError("PayPal subscription {SubscriptionId} was approved after its discount code expired at {ExpiresAt}; cancelling it, its payments need a manual refund",
            remote.Id, request.ExpiresAt);
        if (remote.Status is "ACTIVE" or "SUSPENDED")
            await PayPalApi.Cancel(remote.Id, ExpiredCodeCancelReason);
        request.State = PaymentRequest.Status.REJECTED;
        request.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();
    }

    private async Task<string> PayPalCheckoutRejection(PayPalSubscription remote, PaymentRequest request, TopUpProduct plan)
    {
        if (request == null || plan == null)
            return "no checkout of a subscription plan was recorded for it";
        if (!await PayPalPlans.IsPlanOf(remote.PlanId, plan))
            return $"plan {remote.PlanId} is not the PayPal plan of {plan.Slug}";
        if (remote.CustomId != PayPalCustomId(plan.Id, request.User.ExternalId))
            return $"custom_id {remote.CustomId} does not match the checkout";
        return null;
    }

    private async Task ApplyPayPalState(UserSubscription subscription, PayPalSubscription remote)
    {
        subscription.Status = remote.Status switch
        {
            "ACTIVE" => "active",
            "SUSPENDED" => "paused",
            "CANCELLED" => "cancelled",
            "EXPIRED" => "expired",
            _ => "pending"
        };
        if (ParseTime(remote.BillingInfo?.NextBillingTime) is DateTime next)
            subscription.RenewsAt = next;
        subscription.EndsAt = subscription.Status switch
        {
            // PayPal cancels immediately; the already paid period still runs out.
            "cancelled" => subscription.EndsAt ?? await AccessExpiry(subscription) ?? subscription.RenewsAt,
            "expired" => subscription.EndsAt ?? DateTime.UtcNow,
            _ => null
        };
        subscription.ProviderPlanId = remote.PlanId ?? subscription.ProviderPlanId;
        subscription.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// ACTIVE, or a later state of a subscription that was active (a payment may complete just before a cancellation)
    /// </summary>
    private static bool IsPastApproval(string status) => status is "ACTIVE" or "SUSPENDED" or "CANCELLED" or "EXPIRED";

    private static DateTime? ParseTime(string time) =>
        DateTime.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed : null;

    private static decimal? ParseMoney(string value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private sealed class PayPalProvider(SubscriptionService service) : ISubscriptionProvider
    {
        public string Slug => SubscriptionProviders.PayPal;

        // PayPal cancellation is immediate and final.
        public bool CanResume(UserSubscription subscription) => false;

        public bool CanChangePlan(UserSubscription subscription) => false;

        public bool OffersPlan(TopUpProduct plan) => false;

        public async Task CancelAtPeriodEnd(UserSubscription subscription)
        {
            await service.PayPalApi.Cancel(subscription.ExternalId, PayPalCancelReason);
            subscription.Status = "cancelled";
            subscription.EndsAt = await service.AccessExpiry(subscription) ?? subscription.RenewsAt;
            subscription.UpdatedAt = DateTime.UtcNow;
        }

        public Task Resume(UserSubscription subscription) =>
            throw new ApiException("PayPal subscriptions can not be resumed.");

        public Task<SubscriptionChangeResult> ChangePlan(UserSubscription subscription, TopUpProduct target) =>
            throw new ApiException("PayPal subscriptions can not change plans.");

        public async Task<IReadOnlyList<SubscriptionInvoice>> GetInvoices(UserSubscription subscription)
        {
            var transactions = await service.PayPalApi.Transactions(subscription.ExternalId,
                subscription.CreatedAt.AddDays(-1), DateTime.UtcNow.AddDays(1));
            return transactions.OrderBy(t => t.Time).Select((t, index) => ToInvoice(t, index == 0)).Reverse().ToList();
        }

        private static SubscriptionInvoice ToInvoice(PayPalTransaction transaction, bool first)
        {
            var gross = transaction.AmountWithBreakdown?.GrossAmount;
            var total = (int)SubscriptionPricing.ToCents(ParseMoney(gross?.Value) ?? 0);
            var status = transaction.Status switch
            {
                "COMPLETED" => "paid", "REFUNDED" => "refunded", "PARTIALLY_REFUNDED" => "partial_refund",
                _ => transaction.Status?.ToLowerInvariant()
            };
            return new SubscriptionInvoice
            {
                Id = transaction.Id, BillingReason = first ? "initial" : "renewal", Status = status, StatusFormatted = status,
                Refunded = status == "refunded", Currency = gross?.CurrencyCode, Total = total, Subtotal = total,
                TotalFormatted = gross?.Value + " " + gross?.CurrencyCode,
                CreatedAt = ParseTime(transaction.Time) ?? DateTime.UtcNow, UpdatedAt = ParseTime(transaction.Time) ?? DateTime.UtcNow
            };
        }

        public async Task<RefundResponse> RefundInvoice(UserSubscription subscription, SubscriptionInvoice invoice, int? amountCents)
        {
            var refund = await service.PayPalApi.RefundSale(invoice.Id, amountCents / 100m, invoice.Currency);
            var refunded = (int)SubscriptionPricing.ToCents(ParseMoney(refund?.Amount?.Total) ?? 0);
            var full = refund?.State == "completed" && (amountCents == null || refunded >= invoice.Total);
            return new RefundResponse
            {
                Id = invoice.Id, Refunded = full, RefundedAmount = refunded,
                Status = refund?.State != "completed" ? refund?.State : full ? "refunded" : "partial_refund"
            };
        }
    }
}
