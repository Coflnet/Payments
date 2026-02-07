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
        /// Licenses owned by one user
        /// </summary>
        public DbSet<License> Licenses { get; set; }
        /// <summary>
        /// User subscriptions
        /// </summary>
        public DbSet<UserSubscription> Subscriptions { get; set; }
        /// <summary>
        /// Trial usage records to prevent multiple trials per user/product
        /// </summary>
        public DbSet<TrialUsage> TrialUsages { get; set; }
        /// <summary>
        /// Creator codes for discounts and revenue attribution
        /// </summary>
        public DbSet<CreatorCode> CreatorCodes { get; set; }
        /// <summary>
        /// Revenue records from creator code usage
        /// </summary>
        public DbSet<CreatorCodeRevenue> CreatorCodeRevenues { get; set; }

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
                entity.HasIndex(e => new { e.Provider, e.State, e.UpdatedAt });
                entity.HasIndex(e => new { e.State, e.UpdatedAt });
            });
            modelBuilder.Entity<FiniteTransaction>(entity =>
            {
                entity.HasIndex(e => e.Reference);
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
                entity.HasIndex(e => e.Expires);
            });
            modelBuilder.Entity<License>(entity =>
            {
                entity.ToTable("Licenses");
                entity.HasIndex(e => new { e.UserId, e.TargetId, e.Expires });
                entity.HasIndex(e => new { e.TargetId, e.Expires });
                entity.HasIndex(e => new { e.UserId, e.TargetId, e.ProductId }).IsUnique();
            });

            modelBuilder.Entity<UserSubscription>(entity =>
            {
                entity.HasIndex(e => e.ExternalId);
            });

            modelBuilder.Entity<TrialUsage>(entity =>
            {
                entity.HasIndex(e => new { e.UserId, e.ProductId }).IsUnique();
                entity.HasIndex(e => e.TrialStartedAt);
            });

            modelBuilder.Entity<CreatorCode>(entity =>
            {
                entity.HasIndex(e => e.Code).IsUnique();
                entity.HasIndex(e => e.CreatorUserId);
                entity.HasIndex(e => e.IsActive);
            });

            modelBuilder.Entity<CreatorCodeRevenue>(entity =>
            {
                entity.HasIndex(e => e.CreatorCodeId);
                entity.HasIndex(e => e.PurchasedAt);
                entity.HasIndex(e => e.IsPaidOut);
                entity.HasIndex(e => new { e.CreatorCodeId, e.PurchasedAt });
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