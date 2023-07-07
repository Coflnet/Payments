using Coflnet.Payments.Models;
using System.Threading.Tasks;

namespace Coflnet.Payments.Services
{
    /// <summary>
    /// Default producer doing nothing
    /// </summary>
    public class TransactionEventProducer : ITransactionEventProducer, IPaymentEventProducer
    {
        /// <summary>
        /// Produce an event into some event queue to be consumed by other services
        /// </summary>
        /// <param name="transactionEvent">the event to produce</param>
        /// <returns></returns>
        public Task ProduceEvent(TransactionEvent transactionEvent)
        {
            return Task.CompletedTask;
        }

        public Task ProduceEvent(PaymentEvent paymentEvent)
        {
            return Task.CompletedTask;
        }
    }
}