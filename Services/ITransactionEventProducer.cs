using Coflnet.Payments.Models;
using System.Threading.Tasks;

namespace Coflnet.Payments.Services
{
    public interface ITransactionEventProducer
    {
        /// <summary>
        /// Produce an event into some event queue to be consumed by other services
        /// </summary>
        /// <param name="transactionEvent">the event to produce</param>
        /// <returns></returns>
        Task ProduceEvent(TransactionEvent transactionEvent);
    }
}