using System;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Coflnet.Payments.Services
{
    public class RuleEngineTests
    {
        private SqliteConnection _connection;
        private DbContextOptions<PaymentContext> _contextOptions;

        [Test]
        public async Task SqliteInMemoryBloggingControllerTest()
        {
            // Create and open a connection. This creates the SQLite in-memory database, which will persist until the connection is closed
            // at the end of the test (see Dispose below).
            _connection = new SqliteConnection("Filename=:memory:");
            _connection.Open();

            // These options will be used by the context instances in this test suite, including the connection opened above.
            _contextOptions = new DbContextOptionsBuilder<PaymentContext>()
                .UseSqlite(_connection)
                .Options;

            // Create the schema and seed some data
            using var context = new PaymentContext(_contextOptions);

            context.Database.Migrate();

            context.Users.Add(
                new User() {ExternalId = "1", Balance = 0, Id = 1});
            context.SaveChanges();
            var users = await context.Users.ToListAsync();
            Assert.AreEqual(1, users.Count);
        }

        PaymentContext CreateContext() => new PaymentContext(_contextOptions);

        public void Dispose() => _connection.Dispose();
    }

}