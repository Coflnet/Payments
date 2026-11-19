using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Coflnet.Payments.Services;

/// <summary>
/// Shortly before a 13 or 52 week term ends the buyer is reminded that the subscription then continues every 4 weeks (and
/// what one more term of the same length would cost instead). One reminder per term end, published like payments (topic
/// sky-payments, <see cref="PaymentConfirmationTypes.SubscriptionRenewalReminder"/>); the consumer sends the email and dedupes
/// by the transaction id, so a reminder is recorded as sent only after it was published (at least once).
/// </summary>
public partial class SubscriptionService
{
    /// <summary>A term ending within this many days is reminded of</summary>
    internal const int RenewalReminderDays = 35;
    /// <summary>Default of SUBSCRIPTIONS:RENEWAL_REMINDERS_PER_RUN: a backlog must not flood the email queue ahead of purchase confirmations</summary>
    internal const int DefaultRenewalRemindersPerRun = 50;

    /// <summary>
    /// Publishes up to <paramref name="maxPerRun"/> due renewal reminders, the earliest term ends first. A reminder that can not
    /// be built is logged and skipped; a failed or timed out publish (Kafka unreachable) stops the run. The next run retries both.
    /// Returns how many were published.
    /// </summary>
    public async Task<int> SendRenewalReminders(IPaymentEventProducer producer, TimeSpan publishTimeout,
        int maxPerRun = DefaultRenewalRemindersPerRun, CancellationToken cancellation = default)
    {
        var now = DateTime.UtcNow;
        var sent = 0;
        foreach (var subscription in await DueRenewalReminders(now, cancellation))
        {
            if (sent >= maxPerRun)
                break;
            cancellation.ThrowIfCancellationRequested();
            var reminder = await TryBuildRenewalReminder(subscription, now, cancellation);
            if (reminder == null)
                continue;
            if (!await TryPublishRenewalReminder(subscription, reminder, producer, publishTimeout, cancellation))
                break;
            sent++;
        }
        return sent;
    }

    private async Task<PaymentEvent> TryBuildRenewalReminder(UserSubscription subscription, DateTime now, CancellationToken cancellation)
    {
        try
        {
            return await RenewalReminder(subscription, now);
        }
        catch (Exception e) when (!cancellation.IsCancellationRequested)
        {
            logger.LogError(e, "The renewal reminder of subscription {SubscriptionId} could not be built; skipped this run", subscription.ExternalId);
            context.ChangeTracker.Clear();
            return null;
        }
    }

    /// <summary>
    /// Direct, active, uncancelled subscriptions in a paid 13 or 52 week term that continues every 4 weeks and ends within
    /// <see cref="RenewalReminderDays"/> days, neither reminded of nor extended for that term end
    /// </summary>
    internal async Task<List<UserSubscription>> DueRenewalReminders(DateTime now, CancellationToken cancellation = default)
    {
        var until = now.AddDays(RenewalReminderDays);
        var due = await context.Subscriptions.AsNoTracking()
            .Include(s => s.User).Include(s => s.Product).Include(s => s.RenewalProduct)
            .Where(s => (s.ProviderSlug == SubscriptionProviders.Stripe || s.ProviderSlug == SubscriptionProviders.PayPal)
                && s.Status == "active" && s.EndsAt == null && s.RenewalProduct != null
                && s.Product.OwnershipSeconds > SubscriptionRenewal.MaxFourWeekSeconds
                && s.RenewsAt > now && s.RenewsAt <= until
                && (s.RenewalReminderSentFor == null || s.RenewalReminderSentFor != s.RenewsAt)
                && (s.ExtensionStartsAt == null || s.ExtensionStartsAt != s.RenewsAt)
                // by user first: FiniteTransactions has an index on the user, not on the subscription id
                && context.FiniteTransactions.Any(t => t.User.Id == s.User.Id && t.SubscriptionId == s.ExternalId))
            .OrderBy(s => s.RenewsAt).ToListAsync(cancellation);
        return due.Where(InFirstLongTerm).ToList();
    }

    private async Task<bool> TryPublishRenewalReminder(UserSubscription subscription, PaymentEvent reminder, IPaymentEventProducer producer,
        TimeSpan publishTimeout, CancellationToken cancellation)
    {
        try
        {
            await producer.ProduceEvent(reminder).WaitAsync(publishTimeout, cancellation);
        }
        catch (Exception e) when (!cancellation.IsCancellationRequested)
        {
            logger.LogWarning(e, "Renewal reminder {ReminderId} could not be published; retrying with the next run", reminder.PaymentProviderTransactionId);
            return false;
        }
        var termEnd = subscription.RenewsAt;
        await context.Subscriptions.Where(s => s.Id == subscription.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.RenewalReminderSentFor, termEnd), cancellation);
        logger.LogInformation("Published renewal reminder {ReminderId}", reminder.PaymentProviderTransactionId);
        return true;
    }

    /// <summary>The reminder event (contract with the consumer: see <see cref="PaymentEvent"/>)</summary>
    internal async Task<PaymentEvent> RenewalReminder(UserSubscription subscription, DateTime now)
    {
        var provider = SubscriptionProviders.Normalize(subscription.ProviderSlug);
        var termEnd = subscription.RenewsAt;
        var reminder = new PaymentEvent
        {
            ConfirmationType = PaymentConfirmationTypes.SubscriptionRenewalReminder,
            PaymentProvider = provider,
            PaymentMethod = subscription.ManagedPayments ? "stripe_managed_payments" : provider,
            PaymentProviderTransactionId = RenewalReminderId(subscription.ExternalId, termEnd),
            PayedAmount = 0,
            Timestamp = now,
            UserId = subscription.User.ExternalId,
            Email = await LatestBuyerEmail(subscription),
            ProductId = subscription.Product.Id.ToString(CultureInfo.InvariantCulture),
            ProductTitle = subscription.Product.Title,
            Currency = subscription.Currency,
            LegalLocale = (await LatestDeclaration(subscription))?.Locale,
            SubscriptionExternalId = subscription.ExternalId
        };
        await ApplyReminderTerms(reminder, subscription, termEnd);
        return reminder;
    }

    /// <summary>The ending term, the 4-week continuation and, when it can be extended, the price of one more term</summary>
    private async Task ApplyReminderTerms(PaymentEvent reminder, UserSubscription subscription, DateTime termEnd)
    {
        reminder.SubscriptionBillingWeeks = IntervalWeeks(subscription.Product.OwnershipSeconds);
        reminder.SubscriptionRenewalWeeks = SubscriptionRenewal.RenewalWeeks;
        reminder.SubscriptionRenewalAmount = subscription.RenewalPriceCents is long renewal
            ? StripeAmounts.FromMinorUnits(renewal, subscription.Currency) : null;
        reminder.RenewalTaxTreatment = PriceTaxTreatment(subscription);
        reminder.SubscriptionTermEndsAt = termEnd;
        var extension = await CanExtendLongTerm(subscription) ? await ExtensionPriceCents(subscription) : null;
        reminder.SubscriptionExtensionAmount = extension is long cents ? StripeAmounts.FromMinorUnits(cents, subscription.Currency) : null;
    }

    internal static string RenewalReminderId(string subscriptionId, DateTime termEnd) =>
        $"renewal-reminder:{subscriptionId}:{termEnd.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";

    /// <summary>The buyer email of the subscription's latest payment record (a PayPal continuation: also the replaced subscription's)</summary>
    private Task<string> LatestBuyerEmail(UserSubscription subscription)
    {
        var ids = new[] { subscription.ExternalId, subscription.PreviousSubscriptionId ?? subscription.ExternalId };
        return context.PaymentRecords
            .Where(r => r.IsSubscriptionPayment && ids.Contains(r.ExternalTransactionId) && r.BuyerEmail != null)
            .OrderByDescending(r => r.PaidAt).Select(r => r.BuyerEmail).FirstOrDefaultAsync();
    }
}
