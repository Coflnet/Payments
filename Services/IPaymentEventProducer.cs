using System.Threading.Tasks;
using Coflnet.Payments.Models;

namespace Coflnet.Payments.Services
{
    public interface IPaymentEventProducer
    {
        /// <summary>
        /// Produce an event into some event queue to be consumed by other services
        /// </summary>
        Task ProduceEvent(PaymentEvent paymentEvent);
    }
}