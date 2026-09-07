namespace Coflnet.Payments.Models;

/// <summary>Effective access, including revocable slots, without changing billing ownership.</summary>
public class OwnershipAccess
{
    public string ProductSlug { get; set; }
    public DateTime ExpiresAt { get; set; }
    public string OwnerId { get; set; }
    public long? SlotId { get; set; }
    public string MinecraftUuid { get; set; }
    public bool CanManage { get; set; }
}
