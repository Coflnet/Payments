using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Coflnet.Payments.Services;

/// <summary>
/// Status changes of a term switch. Each one is a conditional update of the row's status (it only applies from the expected status),
/// so concurrent handlers (webhooks of the new PayPal subscription, the hourly run, the buyer's next request) never overwrite each
/// other's outcome. The promotion use counted for a switch is given back exactly when the switch stops being pending without being
/// completed (failed, expired), and counted again when such a switch is completed after all (paid or approved late).
/// </summary>
public partial class SubscriptionService
{
    /// <summary>The switch was not applied: it no longer blocks another one and its promotion use is given back</summary>
    private async Task FailTermSwitch(SubscriptionTermSwitch row)
    {
        await context.SaveChangesAsync();
        await EndPendingTermSwitch(row, TermSwitchStatus.Failed);
    }

    /// <summary>Pending → <paramref name="status"/> (failed, expired); false when it was no longer pending</summary>
    private async Task<bool> EndPendingTermSwitch(SubscriptionTermSwitch row, string status)
    {
        if (!await ChangeTermSwitchStatus(row, status, TermSwitchStatus.Pending))
            return false;
        if (row.PromotionCode != null)
            await DiscountCodes.Release(row.PromotionCode);
        return true;
    }

    /// <summary>
    /// The switch was paid or approved: completed (false when it is in none of the given states). One that had ended meanwhile
    /// (<paramref name="ended"/>: its promotion use was given back) counts its promotion again; if the promotion is used up by then the
    /// buyer keeps the price paid (logged).
    /// </summary>
    private async Task<bool> CompleteTermSwitch(SubscriptionTermSwitch row, params string[] ended)
    {
        if (await ChangeTermSwitchStatus(row, TermSwitchStatus.Completed, TermSwitchStatus.Pending))
            return true;
        if (ended.Length > 0 && await ChangeTermSwitchStatus(row, TermSwitchStatus.Completed, ended))
        {
            await RedeemPromotionAgain(row);
            return true;
        }
        return await IsStoredAsCompleted(row);
    }

    /// <summary>Completed already (by an earlier attempt whose follow-up was lost); the loaded row follows</summary>
    private async Task<bool> IsStoredAsCompleted(SubscriptionTermSwitch row)
    {
        var stored = await context.SubscriptionTermSwitches.AsNoTracking().Where(r => r.Id == row.Id).Select(r => r.Status).FirstOrDefaultAsync();
        if (stored != TermSwitchStatus.Completed)
            return false;
        AcceptStoredValue(row, r => r.Status, stored);
        return true;
    }

    private async Task RedeemPromotionAgain(SubscriptionTermSwitch row)
    {
        if (row.PromotionCode == null || await DiscountCodes.Redeem(row.PromotionCode))
            return;
        logger.LogWarning("Term switch {SwitchId} was paid at promotion {Code} after it ended unpaid, but the promotion is used up now; the price paid stands",
            row.Id, row.PromotionCode);
    }

    /// <summary>
    /// Sets the status only when it is one of <paramref name="from"/> in the database (a conditional update); the loaded row follows
    /// without being marked changed, so a later save never writes a stale status
    /// </summary>
    private async Task<bool> ChangeTermSwitchStatus(SubscriptionTermSwitch row, string status, params string[] from)
    {
        var completedAt = status == TermSwitchStatus.Completed ? DateTime.UtcNow : (DateTime?)null;
        var changed = await context.SubscriptionTermSwitches.Where(r => r.Id == row.Id && from.Contains(r.Status))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, status).SetProperty(r => r.CompletedAt, r => completedAt ?? r.CompletedAt)) == 1;
        if (changed)
            AcceptStoredValue(row, r => r.Status, status);
        if (changed && completedAt != null)
            AcceptStoredValue(row, r => r.CompletedAt, completedAt);
        return changed;
    }

    private void AcceptStoredValue<T>(SubscriptionTermSwitch row, System.Linq.Expressions.Expression<Func<SubscriptionTermSwitch, T>> property, T value)
    {
        var entry = context.Entry(row);
        if (entry.State == EntityState.Detached)
        {
            entry.Property(property).CurrentValue = value;
            return;
        }
        entry.Property(property).CurrentValue = value;
        entry.Property(property).OriginalValue = value;
    }

    /// <summary>
    /// Hourly: switches the buyer never paid or approved stop being pending (their promotion use is given back). Each is checked under
    /// the lock of its subscription; a PayPal switch the payer approved meanwhile is left to its activation
    /// (<see cref="LinkApprovedPayPalTermSwitches"/>), an unapproved one is cancelled at PayPal.
    /// </summary>
    private async Task ExpireAbandonedTermSwitches(CancellationToken cancellation)
    {
        var now = DateTime.UtcNow;
        var expired = await context.SubscriptionTermSwitches.AsNoTracking()
            .Where(r => r.Status == TermSwitchStatus.Pending && r.ExpiresAt < now)
            .Select(r => new { r.Id, r.SubscriptionId }).ToListAsync(cancellation);
        foreach (var row in expired)
        {
            cancellation.ThrowIfCancellationRequested();
            await TryExpireTermSwitch(row.Id, row.SubscriptionId);
        }
    }

    private async Task TryExpireTermSwitch(long id, string subscriptionId)
    {
        try
        {
            await WithSubscriptionLock(subscriptionId, () => ExpireTermSwitchLocked(id));
        }
        catch (SubscriptionBusyException)
        {
            logger.LogDebug("Subscription {SubscriptionId} is being changed; its expired term switch {SwitchId} is closed by the next run", subscriptionId, id);
        }
        catch (Exception e)
        {
            context.ChangeTracker.Clear();
            logger.LogError(e, "Expired term switch {SwitchId} of subscription {SubscriptionId} could not be closed; retried by the next run", id, subscriptionId);
        }
    }

    private async Task<bool> ExpireTermSwitchLocked(long id)
    {
        var row = await context.SubscriptionTermSwitches.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
        if (row?.Status != TermSwitchStatus.Pending || row.ExpiresAt >= DateTime.UtcNow)
            return false;
        var approved = row.Provider == SubscriptionProviders.PayPal && IsApprovedAtPayPal(await PayPalSwitchStatus(row.NewSubscriptionId));
        if (approved || !await EndPendingTermSwitch(row, TermSwitchStatus.Expired))
            return false;
        logger.LogInformation("Term switch {SwitchId} of subscription {SubscriptionId} expired unpaid", row.Id, row.SubscriptionId);
        if (row.Provider == SubscriptionProviders.PayPal)
            await CancelExpiredPayPalSwitch(row);
        return true;
    }

    /// <summary>
    /// The payer may approve while the switch expires: right before cancelling, the row must still be expired (not completed by the
    /// approval's activation) and PayPal must still report it unapproved; otherwise it is left to its activation, which accepts it
    /// </summary>
    private async Task CancelExpiredPayPalSwitch(SubscriptionTermSwitch row)
    {
        var stored = await context.SubscriptionTermSwitches.AsNoTracking().Where(r => r.Id == row.Id).Select(r => r.Status).FirstOrDefaultAsync();
        if (stored != TermSwitchStatus.Expired)
            return;
        var status = await PayPalSwitchStatus(row.NewSubscriptionId);
        if (IsApprovedAtPayPal(status))
        {
            logger.LogInformation("Expired term switch {SwitchId} was approved meanwhile; left to its activation", row.Id);
            return;
        }
        await TryCancelUnapprovedPayPal(row.NewSubscriptionId, status);
    }
}
