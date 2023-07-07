namespace Coflnet.Payments.Services
{
    public class PaymentEvent
    {
        public double PayedAmount { get; set; }
        public string ProductId { get; set; }
        public string UserId { get; set; }
        public string CountryCode { get; set; }
        public string PostalCode { get; set; }
        public string Currency { get; set; }
        public string PaymentMethod { get; set; }
        public string PaymentProvider { get; set; }
        public string PaymentProviderTransactionId { get; set; }
        public DateTime Timestamp { get; set; }
    }
}