using Microsoft.EntityFrameworkCore;

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
            });

            modelBuilder.Entity<PurchaseableProduct>(entity =>
            {
                entity.HasIndex(e => e.Slug).IsUnique();
            });
            modelBuilder.Entity<TopUpProduct>(entity =>
            {
                entity.HasIndex(e => new { e.Slug, e.ProviderSlug }).IsUnique();
            });
        }
    }
}