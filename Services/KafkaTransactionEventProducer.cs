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
    public class KafkaTransactionEventProducer : ITransactionEventProducer
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
            logger.LogInformation("activated Kafka event logger with hosts " + producerConfig.BootstrapServers);
            CreateTopicIfNotExists();
        }

        private void CreateTopicIfNotExists()
        {
            logger.LogInformation(JsonConvert.SerializeObject(new AdminClientConfig(producerConfig)));
            var adminClient = new AdminClientBuilder(producerConfig).Build();
            var meta = adminClient.GetMetadata(configuration["TRANSACTION_TOPIC:NAME"], TimeSpan.FromSeconds(10));
            if (meta.Topics.Count == 0 || meta.Topics[0].Error.Code != ErrorCode.NoError)
            {
                logger.LogWarning("Topic " + configuration["TRANSACTION_TOPIC:NAME"] + " does not exist, creating it");
                adminClient.CreateTopicsAsync(new TopicSpecification[] { new TopicSpecification() {
                    Name = configuration["TRANSACTION_TOPIC:NAME"],
                    NumPartitions = configuration.GetValue<int>("TRANSACTION_TOPIC:NUM_PARTITIONS"),
                    ReplicationFactor = configuration.GetValue<short>("TRANSACTION_TOPIC:REPLICATION_FACTOR"),
                     } }).Wait();
            }
            else
                logger.LogInformation("Metadata for topic " + configuration["TRANSACTION_TOPIC:NAME"] + " is " + JsonConvert.SerializeObject(meta.Topics[0]));
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
            Console.WriteLine("SASL username is " + producerConfig.SaslUsername);
            Console.WriteLine("SASL password is set " + string.IsNullOrWhiteSpace(producerConfig.SaslPassword));
            if(!string.IsNullOrEmpty(producerConfig.SaslUsername))
            {
                if(!string.IsNullOrEmpty(producerConfig.SslKeyLocation))
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
            UpdateConfig();
            using (var p = new ProducerBuilder<Null, TransactionEvent>(producerConfig).SetValueSerializer(serializer).Build())
            {
                var result = await p.ProduceAsync(configuration["TRANSACTION_TOPIC:NAME"], new Message<Null, TransactionEvent>()
                {
                    Value = transactionEvent,
                    Timestamp = new Timestamp(transactionEvent.Timestamp)
                });
                logger.LogInformation("Produced event " + result.TopicPartitionOffset + " " + JsonConvert.SerializeObject(transactionEvent));
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