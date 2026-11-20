using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.EntityFrameworkCore;

namespace Coflnet.Payments.Services;

/// <summary>
/// The dispute state of a <see cref="PaymentRecord"/>: <see cref="PaymentRecordStatus.Disputed"/> from the chargeback until it is
/// won. A refunded record stays refunded (the money went back to the buyer once already).
/// </summary>
public static class PaymentRecordDispute
{
    /// <summary>The record of a provider payment, by order id or transaction id; null when none was written</summary>
    public static Task<PaymentRecord> Find(PaymentContext db, string provider, string paymentId) => db.PaymentRecords
        .Where(r => r.Provider == provider && (r.ExternalOrderId == paymentId || r.ExternalTransactionId == paymentId))
        .OrderBy(r => r.Id).FirstOrDefaultAsync();

    /// <summary>Whether the dispute was already settled by a refund, so there is nothing left to reverse</summary>
    public static bool IsRefunded(PaymentRecord record) => record?.Status == PaymentRecordStatus.Refunded;

    public static void Open(PaymentRecord record)
    {
        if (record != null && !IsRefunded(record))
            record.Status = PaymentRecordStatus.Disputed;
    }

    /// <summary>Back to paid; a partial refund made before the dispute stays visible</summary>
    public static void Won(PaymentRecord record)
    {
        if (record?.Status != PaymentRecordStatus.Disputed)
            return;
        record.Status = record.RefundedAmount > 0 ? PaymentRecordStatus.PartiallyRefunded : PaymentRecordStatus.Confirmed;
    }
}
