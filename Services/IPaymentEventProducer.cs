using System.Threading.Tasks;

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