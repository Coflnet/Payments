using Coflnet.Payments.Models;
using Microsoft.EntityFrameworkCore;
using System.Linq;
using System.Threading.Tasks;

namespace Coflnet.Payments.Services;

public class TierSlotService(PaymentContext db)
{
    public Task<PurchaseableProduct[]> GetProducts() => db.Products
        .Where(p => p.SlotCount > 0 && !p.Type.HasFlag(Product.ProductType.DISABLED))
        .OrderBy(p => p.Cost).ToArrayAsync();

    public async Task<TierSlotAccess[]> GetOwned(string ownerId) => await db.TierSlots
        .Where(s => s.User.ExternalId == ownerId).OrderBy(s => s.Id)
        .Select(s => new TierSlotAccess(s.Id, ownerId, s.Tier, s.Expires,
            s.AssignedUserId, s.MinecraftUuid, s.Version, true)).ToArrayAsync();

    // A Minecraft assignment is specific to that account. An accompanying user ID
    // restricts it further; it must never turn into access for all of that user's alts.
    public async Task<TierSlotAccess[]> GetAccess(string userId, string minecraftUuid = null)
    {
        minecraftUuid = NormalizeUuid(minecraftUuid);
        if (string.IsNullOrWhiteSpace(userId) && minecraftUuid == null)
            throw new ApiException("an account id or minecraft uuid is required");
        return await db.TierSlots.Where(s => s.Expires > DateTime.UtcNow
            && ((s.MinecraftUuid == null && userId != null && s.AssignedUserId == userId)
                || (minecraftUuid != null && s.MinecraftUuid == minecraftUuid
                    && (userId == null || s.AssignedUserId == null || s.AssignedUserId == userId))))
            .Select(s => new TierSlotAccess(s.Id, s.User.ExternalId, s.Tier, s.Expires,
                s.AssignedUserId, s.MinecraftUuid, s.Version, s.User.ExternalId == userId))
            .ToArrayAsync();
    }

    public async Task Assign(string ownerId, long id, TierSlotAssignment assignment)
    {
        var uuid = NormalizeUuid(assignment.MinecraftUuid);
        var userId = string.IsNullOrWhiteSpace(assignment.UserId) ? null : assignment.UserId.Trim();
        if (userId != null && !await db.Users.AnyAsync(u => u.ExternalId == userId))
            throw new ApiException("recipient account not found");
        var slot = await db.TierSlots.SingleOrDefaultAsync(s => s.Id == id && s.User.ExternalId == ownerId)
            ?? throw new ApiException("slot not found or not owned by this user");
        if (slot.Version != assignment.Version)
            throw new ApiException("slot changed; reload before assigning");
        slot.AssignedUserId = userId;
        slot.MinecraftUuid = uuid;
        slot.Version++;
        await db.SaveChangesAsync();
    }

    internal static string NormalizeUuid(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (!Guid.TryParseExact(value, "N", out var uuid) && !Guid.TryParseExact(value, "D", out uuid))
            throw new ApiException("invalid minecraft uuid");
        if (uuid == Guid.Empty)
            throw new ApiException("invalid minecraft uuid");
        return uuid.ToString("N");
    }

    internal async Task ApplyPurchase(User owner, Product product, long transactionId,
        int count, long seconds, long[] slotIds)
    {
        if (product.SlotCount < 1 || product.SlotCount > 100 || seconds <= 0
            || product.SlotTier is not ("starter_premium" or "premium" or "premium_plus"))
            throw new ApiException("invalid slot product");
        var capacity = checked(product.SlotCount * count);
        if (capacity > 100)
            throw new ApiException("at most 100 slots can be purchased at once");
        var slots = new System.Collections.Generic.List<TierSlot>();
        if (slotIds != null)
        {
            if (slotIds.Length != capacity || slotIds.Distinct().Count() != capacity)
                throw new ApiException("select one distinct owned slot per package slot");
            slots = await db.TierSlots.Where(s => slotIds.Contains(s.Id)
                && s.UserId == owner.Id && s.Tier == product.SlotTier).ToListAsync();
            if (slots.Count != capacity)
                throw new ApiException("slots must belong to the purchaser and have the package tier");
        }
        else
        {
            slots = Enumerable.Range(0, capacity).Select(_ => new TierSlot
                { UserId = owner.Id, Tier = product.SlotTier }).ToList();
            db.TierSlots.AddRange(slots);
        }
        foreach (var slot in slots)
        {
            slot.Expires = TransactionService.GetNewExpiry(slot.Expires, TimeSpan.FromSeconds(seconds));
            slot.Version++;
            db.TierSlotGrants.Add(new TierSlotGrant
                { TierSlot = slot, TransactionId = transactionId, Seconds = seconds });
        }
        await db.SaveChangesAsync();
    }

    internal async Task<bool> Revert(long transactionId)
    {
        var grants = await db.TierSlotGrants.Where(g => g.TransactionId == transactionId)
            .Include(g => g.TierSlot).ToListAsync();
        foreach (var grant in grants)
        {
            grant.TierSlot.Expires = grant.TierSlot.Expires.AddSeconds(-grant.Seconds);
            grant.TierSlot.Version++;
        }
        return grants.Count > 0;
    }
}
