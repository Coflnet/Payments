using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Coflnet.Payments.Services;

public class PaymentConfirmationOutboxPublisherTests
{
    [Test]
    public async Task ProcessOnePublishesOnceAndMarksRow()
    {
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<PaymentContext>(
            options => options.UseSqlite(connection));
        await using var provider = services.BuildServiceProvider();
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
            await db.Database.EnsureCreatedAsync();
            db.PaymentConfirmationOutbox.Add(new()
            {
                Provider = "coflcoins",
                ProviderTransactionId = "123",
                ConfirmationType = "service_purchase",
                Payload = JsonConvert.SerializeObject(new PaymentEvent
                {
                    UserId = "42",
                    PaymentProviderTransactionId = "123",
                    Timestamp = DateTime.UtcNow
                }),
                CreatedAt = DateTime.UtcNow,
                NextAttemptAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var producer = new RecordingProducer();
        var publisher = new PaymentConfirmationOutboxPublisher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            producer,
            NullLogger<PaymentConfirmationOutboxPublisher>.Instance);

        Assert.That(await publisher.ProcessOneAsync(), Is.True);
        Assert.That(await publisher.ProcessOneAsync(), Is.False);
        Assert.That(producer.Events, Has.Count.EqualTo(1));

        using var verificationScope = provider.CreateScope();
        var row = await verificationScope.ServiceProvider
            .GetRequiredService<PaymentContext>()
            .PaymentConfirmationOutbox.SingleAsync();
        Assert.That(row.PublishedAt, Is.Not.Null);
        Assert.That(row.Attempts, Is.EqualTo(1));
        Assert.That(row.LeaseId, Is.Null);
    }

    private sealed class RecordingProducer : IPaymentEventProducer
    {
        public List<PaymentEvent> Events { get; } = [];

        public Task ProduceEvent(PaymentEvent paymentEvent)
        {
            Events.Add(paymentEvent);
            return Task.CompletedTask;
        }
    }
}
