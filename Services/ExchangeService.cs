using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace Coflnet.Payments.Services
{
    /// <summary>
    /// Converts one currency into another
    /// </summary>
    public class ExchangeService
    {
        private ILogger<ExchangeService> logger;
        private ConversionRate conversionRate = new ConversionRate();

        /// <summary>
        /// Crates a new instance of the <see cref="ExchangeService"/>
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="config"></param>
        public ExchangeService(
            ILogger<ExchangeService> logger,
            IConfiguration config)
        {
            this.logger = logger;
            config.Bind("CONVERSION_RATE",conversionRate);
            System.Console.WriteLine(Newtonsoft.Json.JsonConvert.SerializeObject(conversionRate));
            System.Console.WriteLine(config.GetValue<decimal>("CONVERSION_RATE:Eur"));
        }

        /// <summary>
        /// Converts an amount to euro
        /// </summary>
        /// <param name="euro">The amount to convert</param>
        /// <returns></returns>
        public decimal FromEur(decimal euro)
        {
            return euro * conversionRate.Amount / conversionRate.Eur;
        }

        /// <summary>
        /// Converts euros to an amount
        /// </summary>
        /// <param name="amount">The euro amount to convert</param>
        /// <returns></returns>
        public decimal ToEur(decimal amount)
        {
            return amount * conversionRate.Eur / conversionRate.Amount;
        }

        public class ConversionRate
        {
            public decimal Eur { get; set; }
            public decimal Amount { get; set; }
        }
    }
}