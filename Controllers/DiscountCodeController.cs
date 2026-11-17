using System.Collections.Generic;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services;
using Microsoft.AspNetCore.Mvc;

namespace Payments.Controllers;

/// <summary>
/// Manages the local discount codes used by Stripe, PayPal and CoinGate checkouts
/// (Lemon Squeezy codes are managed in Lemon Squeezy)
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class DiscountCodeController : ControllerBase
{
    private readonly DiscountCodeService discountCodes;

    public DiscountCodeController(DiscountCodeService discountCodes)
    {
        this.discountCodes = discountCodes;
    }

    /// <summary>
    /// All discount codes, newest first
    /// </summary>
    [HttpGet]
    public Task<List<DiscountCode>> List() => discountCodes.List();

    /// <summary>
    /// Creates or replaces a discount code (matched case-insensitively by code)
    /// </summary>
    [HttpPost]
    public Task<DiscountCode> Upsert([FromBody] DiscountCode code) => discountCodes.Upsert(code);
}
