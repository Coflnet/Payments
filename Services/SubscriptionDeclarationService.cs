using System;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Coflnet.Payments.Services;

/// <summary>
/// Validates and stores the early performance declaration of subscription checkouts, with the same rules as the
/// declaration of purchases with coins (<see cref="TransactionService"/>): the same validation, the same enforcement
/// switch (LEGAL:ENFORCE_SERVICE_PERFORMANCE_DECLARATION; off: a missing declaration is only logged).
/// </summary>
public class SubscriptionDeclarationService(PaymentContext db, IConfiguration config, ILogger logger)
{
    private bool Enforced => config?.GetValue<bool>("LEGAL:ENFORCE_SERVICE_PERFORMANCE_DECLARATION") == true;

    /// <summary>
    /// Rejects an invalid declaration (and, when enforced, a missing one) before the checkout is created.
    /// </summary>
    public void Validate(TopUpOptions options, TopUpProduct plan, string userId)
    {
        var declaration = options?.ServicePerformanceDeclaration;
        if (declaration == null)
        {
            RequireOrWarn(userId, plan);
            return;
        }
        if (!Guid.TryParse(declaration.RequestId, out _)
            || declaration.ImmediatePerformanceRequested != declaration.WithdrawalConsequenceAcknowledged)
            throw new ApiException("invalid_service_performance_declaration");
        if (!declaration.ImmediatePerformanceRequested)
        {
            RequireOrWarn(userId, plan);
            return;
        }
        TransactionService.ValidateDeclaration(ToPurchaseRequest(declaration));
    }

    private void RequireOrWarn(string userId, TopUpProduct plan)
    {
        if (Enforced && plan.Price > 0)
            throw new ApiException("service_performance_declaration_required");
        logger.LogWarning("Subscription checkout of user {UserId} has no early-performance declaration", userId);
    }

    /// <summary>
    /// Stores the declaration of a validated checkout, linked to its payment request. Idempotent per declaration id; an id
    /// already used for another checkout is refused.
    /// </summary>
    public async Task Record(TopUpOptions options, User user, TopUpProduct plan, PaymentRequest request)
    {
        var declaration = options?.ServicePerformanceDeclaration;
        if (declaration == null || !declaration.ImmediatePerformanceRequested)
            return;
        var existing = await db.ServicePerformanceDeclarations.SingleOrDefaultAsync(d => d.RequestId == declaration.RequestId);
        if (existing != null)
        {
            await Relink(existing, user, plan, request);
            return;
        }
        db.ServicePerformanceDeclarations.Add(ToEvidence(declaration, user, plan, CheckoutReference(request.Id), request.Id, DateTime.UtcNow));
        await db.SaveChangesAsync();
    }

    /// <summary>The purchase reference of the declaration of a subscription checkout</summary>
    public static string CheckoutReference(int paymentRequestId) => $"subscription-checkout-{paymentRequestId}";

    /// <summary>
    /// The purchase reference of the declaration given for one more term of a subscription, starting at <paramref name="termEnd"/>
    /// </summary>
    public static string ExtensionReference(string subscriptionId, DateTime termEnd) =>
        $"subscription-extension-{subscriptionId}-{termEnd.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>
    /// One more term of a subscription is only booked with a valid declaration that requests early performance
    /// </summary>
    public static void ValidateExtension(SubscriptionDeclaration declaration)
    {
        if (declaration == null || !Guid.TryParse(declaration.RequestId, out _)
            || !declaration.ImmediatePerformanceRequested || !declaration.WithdrawalConsequenceAcknowledged)
            throw new ApiException("invalid_service_performance_declaration");
        TransactionService.ValidateDeclaration(ToPurchaseRequest(declaration));
    }

    /// <summary>
    /// Stores the declaration of an extension term (validated with <see cref="ValidateExtension"/>) under
    /// <see cref="ExtensionReference"/>; PayPal extensions link it to the payment request of the new PayPal subscription as well.
    /// Idempotent per declaration id; an id already used for anything else is refused.
    /// </summary>
    public async Task RecordExtension(SubscriptionDeclaration declaration, User user, TopUpProduct plan, string reference,
        int? paymentRequestId, DateTime termStartsAt)
    {
        var existing = await db.ServicePerformanceDeclarations.SingleOrDefaultAsync(d => d.RequestId == declaration.RequestId);
        if (existing == null)
        {
            db.ServicePerformanceDeclarations.Add(ToEvidence(declaration, user, plan, reference, paymentRequestId, termStartsAt));
            await db.SaveChangesAsync();
            return;
        }
        if (existing.UserId != user.ExternalId || existing.PurchaseReference != reference)
            throw new ApiException("service declaration request id already used");
        existing.PaymentRequestId = paymentRequestId ?? existing.PaymentRequestId;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// A buyer whose checkout failed or was abandoned retries with the same declaration: it moves to the new checkout.
    /// Another user's or plan's declaration id is refused, and so is one whose checkout was paid: it is the evidence of
    /// that subscription and must stay linked to it.
    /// </summary>
    private async Task Relink(ServicePerformanceDeclaration existing, User user, TopUpProduct plan, PaymentRequest request)
    {
        if (existing.UserId != user.ExternalId || existing.ProductId != plan.Id)
            throw new ApiException("service declaration request id already used");
        if (existing.PaymentRequestId == request.Id)
            return;
        if (await IsUsed(existing.PaymentRequestId))
            throw new ApiException("service declaration request id already used");
        existing.PaymentRequestId = request.Id;
        existing.PurchaseReference = CheckoutReference(request.Id);
        await db.SaveChangesAsync();
    }

    /// <summary>Paid (possibly since refunded) checkout, or no checkout at all (a declaration of the prepaid flow)</summary>
    private async Task<bool> IsUsed(int? paymentRequestId) => paymentRequestId == null
        || await db.PaymentRequests.AnyAsync(r => r.Id == paymentRequestId && (r.State == PaymentRequest.Status.PAID
            || r.State == PaymentRequest.Status.REFUNDED || r.State == PaymentRequest.Status.REFUND_PENDING));

    private static ServicePurchaseRequest ToPurchaseRequest(SubscriptionDeclaration d) => new()
    {
        Reference = "subscription-checkout", Locale = d.Locale, ImmediatePerformanceRequested = d.ImmediatePerformanceRequested,
        WithdrawalConsequenceAcknowledged = d.WithdrawalConsequenceAcknowledged, DeclarationVersion = d.DeclarationVersion,
        DeclarationText = d.DeclarationText, DeclarationSha256 = d.DeclarationSha256, AgreementId = d.AgreementId,
        AgreementHash = d.AgreementHash, WithdrawalVersion = d.WithdrawalVersion, WithdrawalSha256 = d.WithdrawalSha256,
        TaxCountry = d.TaxCountry, ConsumerRightsRegime = d.ConsumerRightsRegime, VatRateBasisPoints = d.VatRateBasisPoints,
        GrossEurCents = d.GrossEurCents, VatEurCents = d.VatEurCents, OrderDetailsJson = d.OrderDetailsJson, RequestId = d.RequestId
    };

    private static ServicePerformanceDeclaration ToEvidence(SubscriptionDeclaration d, User user, TopUpProduct plan, string reference,
        int? paymentRequestId, DateTime startsAt)
    {
        var now = DateTime.UtcNow;
        return new ServicePerformanceDeclaration
        {
            RequestId = d.RequestId, UserId = user.ExternalId, ProductId = plan.Id, ProductSlug = plan.Slug, Count = 1,
            PurchaseReference = reference, CoinAmount = plan.Cost, StartsAtUtc = startsAt,
            EndsAtUtc = startsAt.AddSeconds(plan.OwnershipSeconds), DeclarationRequired = plan.Price > 0,
            EarlyPerformanceRequested = d.ImmediatePerformanceRequested,
            WithdrawalConsequenceAcknowledged = d.WithdrawalConsequenceAcknowledged,
            Locale = TransactionService.NormalizeLocale(d.Locale, d.ConsumerRightsRegime),
            DeclarationVersion = d.DeclarationVersion, DeclarationText = d.DeclarationText, DeclarationSha256 = d.DeclarationSha256,
            AgreementId = d.AgreementId, AgreementHash = d.AgreementHash, WithdrawalVersion = d.WithdrawalVersion,
            WithdrawalSha256 = d.WithdrawalSha256, TaxCountry = d.TaxCountry, VatRateBasisPoints = d.VatRateBasisPoints,
            GrossEurCents = d.GrossEurCents, VatEurCents = d.VatEurCents, OrderDetailsJson = d.OrderDetailsJson,
            PaymentRequestId = paymentRequestId, CreatedAtUtc = now
        };
    }
}
