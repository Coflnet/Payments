using Coflnet.Payments.Models;
using Coflnet.Payments.Services;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;

namespace Payments.Controllers;

/// <summary>Internal service API. The authenticated gateway supplies the owner identity.</summary>
[ApiController]
[Route("api/tier-slots")]
public class TierSlotsController(TierSlotService slots) : ControllerBase
{
    [HttpGet("products")]
    public Task<PurchaseableProduct[]> GetProducts() => slots.GetProducts();

    [HttpGet("owner/{ownerId}")]
    public Task<TierSlotAccess[]> GetOwned(string ownerId) => slots.GetOwned(ownerId);

    [HttpGet("access")]
    public Task<TierSlotAccess[]> GetAccess(string userId = null, string minecraftUuid = null)
        => slots.GetAccess(userId, minecraftUuid);

    [HttpPut("owner/{ownerId}/{id:long}/assignment")]
    public Task Assign(string ownerId, long id, TierSlotAssignment assignment)
        => slots.Assign(ownerId, id, assignment);
}
