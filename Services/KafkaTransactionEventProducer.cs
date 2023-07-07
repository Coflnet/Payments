using Coflnet.Payments.Models;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Threading.Tasks;

namespace Coflnet.Payments.Services
{
    /// <summary>
    /// produce events into Kafka
    /// </summary>
    public class KafkaTransactionEventProducer : ITransactionEventProducer, IPaymentEventProducer
    {
        IConfiguration configuration;
        ILogger<KafkaTransactionEventProducer> logger;

        private ProducerConfig producerConfig;

        private static TransactionEventSerializer serializer = new TransactionEventSerializer();

        public KafkaTransactionEventProducer(IConfiguration configuration, ILogger<KafkaTransactionEventProducer> logger)
        {
            this.configuration = configuration.GetSection("KAFKA");
            this.logger = logger;
            UpdateConfig();
            logger.LogInformation("activated Kafka event logger with hosts " + configuration["BROKERS"]);
            Task.Run(() => CreateTopicIfNotExists());
        }

        private async Task CreateTopicIfNotExists()
        {
            var adminClient = new AdminClientBuilder(producerConfig).Build();
            await MakeSureTopicExists(adminClient, "TRANSACTION_TOPIC");
            await MakeSureTopicExists(adminClient, "PAYMENT_TOPIC");
        }

        private async Task MakeSureTopicExists(IAdminClient adminClient, string topicConfigKey)
        {
            var configPart = configuration.GetSection(topicConfigKey);
            var meta = adminClient.GetMetadata(configuration["NAME"], TimeSpan.FromSeconds(10));
            if (meta.Topics.Count == 0 || meta.Topics[0].Error.Code != ErrorCode.NoError)
            {
                logger.LogWarning("Topic " + configuration["NAME"] + " does not exist, creating it");
                await adminClient.CreateTopicsAsync(new TopicSpecification[] { new TopicSpecification() {
                    Name = configuration["NAME"],
                    NumPartitions = configuration.GetValue<int>("NUM_PARTITIONS"),
                    ReplicationFactor = configuration.GetValue<short>("REPLICATION_FACTOR"),
                     } });
            }
            else
                logger.LogInformation("Metadata for topic " + configuration["NAME"] + " is " + JsonConvert.SerializeObject(meta.Topics[0]));
        }

        private void UpdateConfig()
        {
            producerConfig = new ProducerConfig
            {
                BootstrapServers = configuration["BROKERS"],
                LingerMs = configuration.GetValue<int>("PRODUCER:LINGER_MS"),
                MessageSendMaxRetries = configuration.GetValue<int>("PRODUCER:RETRIES"),
                //SecurityProtocol = SecurityProtocol.SaslSsl,
                SslCaLocation = configuration["TLS:CA_LOCATION"],
                SslCertificateLocation = configuration["TLS:CERTIFICATE_LOCATION"],
                SslKeyLocation = configuration["TLS:KEY_LOCATION"],
                SaslUsername = configuration["USERNAME"],
                SaslPassword = configuration["PASSWORD"],
            };
            if (!string.IsNullOrEmpty(producerConfig.SaslUsername))
            {
                if (!string.IsNullOrEmpty(producerConfig.SslKeyLocation))
                    producerConfig.SecurityProtocol = SecurityProtocol.SaslSsl;
                else
                    producerConfig.SecurityProtocol = SecurityProtocol.SaslPlaintext;
                producerConfig.SaslMechanism = SaslMechanism.ScramSha256;
            }
        }

        /// <summary>
        /// Produce an event into some event queue to be consumed by other services
        /// </summary>
        /// <param name="transactionEvent">the event to produce</param>
        /// <returns></returns>
        public async Task ProduceEvent(TransactionEvent transactionEvent)
        {
            using (var p = new ProducerBuilder<string, TransactionEvent>(producerConfig).SetValueSerializer(serializer).Build())
            {
                var result = await p.ProduceAsync(configuration["TRANSACTION_TOPIC:NAME"], new Message<string, TransactionEvent>()
                {
                    Key = transactionEvent.UserId,
                    Value = transactionEvent,
                    Timestamp = new Timestamp(transactionEvent.Timestamp)
                });
            }
        }

        /// <summary>
        /// Produces payment events (money payed)
        /// </summary>
        /// <param name="paymentEvent"></param>
        /// <returns></returns>
        public async Task ProduceEvent(PaymentEvent paymentEvent)
        {
            using (var p = new ProducerBuilder<string, PaymentEvent>(producerConfig).SetValueSerializer(new PaymentEventSerializer()).Build())
            {
                var result = await p.ProduceAsync(configuration["PAYMENT_TOPIC:NAME"], new Message<string, PaymentEvent>()
                {
                    Key = paymentEvent.PaymentProviderTransactionId,
                    Value = paymentEvent,
                    Timestamp = new Timestamp(paymentEvent.Timestamp)
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
        public class PaymentEventSerializer : ISerializer<PaymentEvent>
        {
            public byte[] Serialize(PaymentEvent data, SerializationContext context)
            {
                return System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(data));
            }
        }
    }
}