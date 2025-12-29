using System.Collections.Generic;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Payments.Controllers;
[ApiController]
[Route("/api/[controller]")]
public class SubscriptionController : ControllerBase
{
    private ILogger<SubscriptionController> logger;
    private SubscriptionService subscriptionService;

    public SubscriptionController(ILogger<SubscriptionController> logger, SubscriptionService subscriptionService)
    {
        this.logger = logger;
        this.subscriptionService = subscriptionService;
    }

    [HttpGet]
    [Route("u/{userId}")]
    public async Task<IEnumerable<UserSubscription>> GetUserSubscriptions(string userId)
    {
        return await subscriptionService.GetUserSubscriptions(userId);
    }

    [HttpDelete]
    [Route("cancel/{subscriptionId}")]
    public async Task CancelSubscription(string userId, string subscriptionId)
    {
        await subscriptionService.CancelSubscription(userId, subscriptionId);
    }

    /// <summary>
    /// Resume a cancelled subscription that is still in grace period.
    /// A subscription can only be resumed if it was cancelled but hasn't reached its ends_at date yet.
    /// </summary>
    /// <param name="userId">The user ID</param>
    /// <param name="subscriptionId">The external subscription ID from LemonSqueezy</param>
    /// <returns>True if successfully resumed</returns>
    [HttpPost]
    [Route("resume/{subscriptionId}")]
    public async Task<bool> ResumeSubscription(string userId, string subscriptionId)
    {
        return await subscriptionService.ResumeSubscription(userId, subscriptionId);
    }

    /// <summary>
    /// Get all invoices for a subscription
    /// </summary>
    /// <param name="userId">The user ID</param>
    /// <param name="subscriptionId">The external subscription ID from LemonSqueezy</param>
    /// <returns>List of subscription invoices</returns>
    [HttpGet]
    [Route("{subscriptionId}/invoices")]
    public async Task<IEnumerable<SubscriptionInvoice>> GetSubscriptionInvoices(string userId, string subscriptionId)
    {
        return await subscriptionService.GetSubscriptionInvoices(userId, subscriptionId);
    }

    /// <summary>
    /// Generate a download link for a subscription invoice
    /// </summary>
    /// <param name="userId">The user ID</param>
    /// <param name="invoiceId">The invoice ID</param>
    /// <param name="request">Invoice generation request with address details</param>
    /// <returns>Download URL for the invoice PDF</returns>
    [HttpPost]
    [Route("invoice/{invoiceId}/download")]
    public async Task<InvoiceDownloadResponse> GenerateInvoiceDownloadLink(string userId, string invoiceId, [FromBody] GenerateInvoiceRequest request)
    {
        return await subscriptionService.GenerateInvoiceDownloadLink(userId, invoiceId, request);
    }

    /// <summary>
    /// Refund a subscription invoice payment. Refunds can only be issued up to 3 days after the invoice was created.
    /// </summary>
    /// <param name="userId">The user ID</param>
    /// <param name="subscriptionId">The external subscription ID from LemonSqueezy</param>
    /// <param name="invoiceId">The subscription invoice ID</param>
    /// <param name="request">Optional refund amount in cents. If not specified, a full refund will be issued.</param>
    /// <returns>RefundResponse containing the updated invoice details</returns>
    [HttpPost]
    [Route("{subscriptionId}/invoice/{invoiceId}/refund")]
    public async Task<RefundResponse> RefundSubscriptionPayment(string userId, string subscriptionId, string invoiceId, [FromBody] RefundRequest request = null)
    {
        return await subscriptionService.RefundSubscriptionPayment(userId, subscriptionId, invoiceId, request);
    }
}
