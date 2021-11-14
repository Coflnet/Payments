using Coflnet.Payments.Models;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Threading.Tasks;

namespace Coflnet.Payments.Services
{
    /// <summary>
    /// produce events into Kafka
    /// </summary>
    public class KafkaTransactionEventProducer : ITransactionEventProducer
    {
        IConfiguration configuration;


        private ProducerConfig producerConfig;

        private static TransactionEventSerializer serializer = new TransactionEventSerializer();

        public KafkaTransactionEventProducer(IConfiguration configuration, ILogger<KafkaTransactionEventProducer> logger)
        {
            this.configuration = configuration;
            producerConfig = new ProducerConfig
            {
                BootstrapServers = configuration["KAFKA_HOST"],
                LingerMs = 2
            };
            logger.LogInformation("activated Kafka event logger with hosts " + producerConfig.BootstrapServers);
        }

        /// <summary>
        /// Produce an event into some event queue to be consumed by other services
        /// </summary>
        /// <param name="transactionEvent">the event to produce</param>
        /// <returns></returns>
        public async Task ProduceEvent(TransactionEvent transactionEvent)
        {
            using (var p = new ProducerBuilder<Null, TransactionEvent>(producerConfig).SetValueSerializer(serializer).Build())
            {
                await p.ProduceAsync(configuration["KAFKA_TRANSACTION_TOPIC"],new Message<Null, TransactionEvent>(){
                    Value = transactionEvent,
                    Timestamp = new Timestamp(transactionEvent.Timestamp)
                });
            }
        }

        public class TransactionEventSerializer : ISerializer<TransactionEvent>
        {
            public byte[] Serialize(TransactionEvent data, SerializationContext context)
            {
                return System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(data));
            }
        }
    }
}