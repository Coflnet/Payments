using System.ComponentModel.DataAnnotations;
using Newtonsoft.Json;

namespace Coflnet.Payments.Models;

/// <summary>Paid capacity owned by the purchaser, independently of its current beneficiary.</summary>
public class TierSlot
{
    public long Id { get; set; }
    public int UserId { get; set; }
    [JsonIgnore]
    public User User { get; set; }
    [Required, MaxLength(32)]
    public string Tier { get; set; }
    public DateTime Expires { get; set; }
    [MaxLength(32)]
    public string AssignedUserId { get; set; }
    [MaxLength(32)]
    public string MinecraftUuid { get; set; }
    [ConcurrencyCheck]
    public long Version { get; set; }
}

/// <summary>Links purchased time to stable slots, so refunds follow reassignment and renewal.</summary>
public class TierSlotGrant
{
    public long TierSlotId { get; set; }
    public TierSlot TierSlot { get; set; }
    public long TransactionId { get; set; }
    public FiniteTransaction Transaction { get; set; }
    public long Seconds { get; set; }
}

public class TierSlotAssignment
{
    [MaxLength(32)]
    public string UserId { get; set; }
    [MaxLength(36)]
    public string MinecraftUuid { get; set; }
    public long Version { get; set; }
}

public record TierSlotAccess(long Id, string OwnerId, string Tier, DateTime Expires,
    string AssignedUserId, string MinecraftUuid, long Version, bool CanManage);
