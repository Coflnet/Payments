using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Coflnet.Payments.Models
{
    /// <summary>
    /// <see cref="DbContext"/> fro Payments
    /// </summary>
    public class PaymentContext : DbContext
    {
        /// <summary>
        /// Users who can make transactions, buy and own things 
        /// </summary>
        /// <value></value>
        public DbSet<User> Users { get; set; }
        /// <summary>
        /// Transactions created by <see cref="Users"/>
        /// Can not be chaned once created
        /// </summary>
        /// <value></value>
        public DbSet<FiniteTransaction> FiniteTransactions { get; set; }
        /// <summary>
        /// Not yet finished Transactions created by a <see cref="Users"/>
        /// may be updated/delted
        /// </summary>
        /// <value></value>
        public DbSet<PlanedTransaction> PlanedTransactions { get; set; }
        /// <summary>
        /// Purchaseable Proucts
        /// </summary>
        /// <value></value>
        public DbSet<PurchaseableProduct> Products { get; set; }
        /// <summary>
        /// TopUp options
        /// </summary>
        /// <value></value>
        public DbSet<TopUpProduct> TopUpProducts { get; set; }
        /// <summary>
        /// Groups of <see cref="Product"/>
        /// </summary>
        /// <value></value>
        public DbSet<Group> Groups { get; set; }
        /// <summary>
        /// Rules to be applied before purchase
        /// </summary>
        public DbSet<Rule> Rules { get; set; }
        /// <summary>
        /// Things a <see cref="User"/> owns
        /// </summary>
        public DbSet<OwnerShip> OwnerShips { get; set; }
        /// <summary>
        /// Payment requests
        /// </summary>
        public DbSet<PaymentRequest> PaymentRequests { get; set; }

        /// <summary>
        /// Creates a new instance of <see cref="PaymentContext"/>
        /// </summary>
        /// <param name="options"></param>
        public PaymentContext(DbContextOptions<PaymentContext> options)
        : base(options)
        {
        }

        /// <summary>
        /// Configures additional relations and indexes
        /// </summary>
        /// <param name="modelBuilder"></param>
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<User>(entity =>
            {
                entity.HasIndex(e => e.ExternalId).IsUnique();
                entity.HasIndex(e => e.Ip);
            });

            modelBuilder.Entity<PaymentRequest>(entity =>
            {
                entity.HasIndex(e => e.DeviceFingerprint);
                entity.HasIndex(e => e.CreateOnIp);
                entity.HasIndex(e => new { e.CreatedAt, e.SessionId });
            });

            modelBuilder.Entity<PurchaseableProduct>(entity =>
            {
                entity.HasIndex(e => e.Slug).IsUnique();
            });
            modelBuilder.Entity<TopUpProduct>(entity =>
            {
                entity.HasIndex(e => new { e.Slug, e.ProviderSlug }).IsUnique();
            });
            modelBuilder.Entity<Group>(entity =>
            {
                entity.HasIndex(e => e.Slug).IsUnique();
            });
            modelBuilder.Entity<Rule>(entity =>
            {
                entity.HasIndex(e => e.Slug).IsUnique();
            });
            modelBuilder.Entity<OwnerShip>(entity =>
            {
                entity.ToTable("OwnerShip");
            });
        }
    }

    public interface HasId
    {
        public int Id { get; set; }
    }
    public interface HasLongId
    {
        public long Id { get; set; }
    }

    public class OldPaymentContext : DbContext
    {
        /// <summary>
        /// Users who can make transactions, buy and own things 
        /// </summary>
        /// <value></value>
        public DbSet<User> Users { get; set; }
        /// <summary>
        /// Transactions created by <see cref="Users"/>
        /// Can not be chaned once created
        /// </summary>
        /// <value></value>
        public DbSet<FiniteTransaction> FiniteTransactions { get; set; }
        /// <summary>
        /// Not yet finished Transactions created by a <see cref="Users"/>
        /// may be updated/delted
        /// </summary>
        /// <value></value>
        public DbSet<PlanedTransaction> PlanedTransactions { get; set; }
        /// <summary>
        /// Purchaseable Proucts
        /// </summary>
        /// <value></value>
        public DbSet<PurchaseableProduct> Products { get; set; }
        /// <summary>
        /// TopUp options
        /// </summary>
        /// <value></value>
        public DbSet<TopUpProduct> TopUpProducts { get; set; }
        /// <summary>
        /// Groups of <see cref="Product"/>
        /// </summary>
        /// <value></value>
        public DbSet<Group> Groups { get; set; }
        /// <summary>
        /// Rules to be applied before purchase
        /// </summary>
        public DbSet<Rule> Rules { get; set; }
        /// <summary>
        /// Things a <see cref="User"/> owns
        /// </summary>
        public DbSet<OwnerShip> OwnerShips { get; set; }
        /// <summary>
        /// Payment requests
        /// </summary>
        public DbSet<PaymentRequest> PaymentRequests { get; set; }

        /// <summary>
        /// Creates a new instance of <see cref="PaymentContext"/>
        /// </summary>
        /// <param name="options"></param>
        public OldPaymentContext(DbContextOptions<OldPaymentContext> options)
        : base(options)
        {

        }

        /// <summary>
        /// Configures additional relations and indexes
        /// </summary>
        /// <param name="modelBuilder"></param>
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<User>(entity =>
            {
                entity.HasIndex(e => e.ExternalId).IsUnique();
                entity.HasIndex(e => e.Ip);
            });

            modelBuilder.Entity<PaymentRequest>(entity =>
            {
                entity.HasIndex(e => e.DeviceFingerprint);
                entity.HasIndex(e => e.CreateOnIp);
                entity.HasIndex(e => new { e.CreatedAt, e.SessionId });
            });

            modelBuilder.Entity<PurchaseableProduct>(entity =>
            {
                entity.HasIndex(e => e.Slug).IsUnique();
            });
            modelBuilder.Entity<TopUpProduct>(entity =>
            {
                entity.HasIndex(e => new { e.Slug, e.ProviderSlug }).IsUnique();
            });
            modelBuilder.Entity<Group>(entity =>
            {
                entity.HasIndex(e => e.Slug).IsUnique();
            });
            modelBuilder.Entity<Rule>(entity =>
            {
                entity.HasIndex(e => e.Slug).IsUnique();
            });
            modelBuilder.Entity<OwnerShip>(entity =>
            {
                entity.ToTable("OwnerShip");
            });

            // https://stackoverflow.com/a/61243301
            var dateTimeConverter = new ValueConverter<DateTime, DateTime>(
                    v => v.ToUniversalTime(),
                    v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

            var nullableDateTimeConverter = new ValueConverter<DateTime?, DateTime?>(
                v => v.HasValue ? v.Value.ToUniversalTime() : v,
                v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                if (entityType.IsKeyless)
                {
                    continue;
                }

                foreach (var property in entityType.GetProperties())
                {
                    if (property.ClrType == typeof(DateTime))
                    {
                        property.SetValueConverter(dateTimeConverter);
                    }
                    else if (property.ClrType == typeof(DateTime?))
                    {
                        property.SetValueConverter(nullableDateTimeConverter);
                    }
                }
            }
        }
    }
}