using Microsoft.EntityFrameworkCore;
using OTELStdApi.Data.Entities;

namespace OTELStdApi.Data
{
    public class OrderDbContext : DbContext
    {
        public OrderDbContext(DbContextOptions<OrderDbContext> options) : base(options)
        {
        }

        public DbSet<Order> Orders { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Order>(entity =>
            {
                entity.ToTable("Orders");

                entity.HasKey(e => e.Id);

                entity.Property(e => e.OrderNumber)
                    .IsRequired()
                    .HasMaxLength(100);

                entity.HasIndex(e => e.OrderNumber)
                    .IsUnique()
                    .HasDatabaseName("IX_Orders_OrderNumber");

                entity.Property(e => e.CustomerId)
                    .IsRequired()
                    .HasMaxLength(200);

                entity.HasIndex(e => e.CustomerId)
                    .HasDatabaseName("IX_Orders_CustomerId");

                entity.Property(e => e.CustomerType)
                    .IsRequired()
                    .HasMaxLength(100);

                entity.Property(e => e.TotalAmount)
                    .IsRequired()
                    .HasColumnType("decimal(18,2)");

                entity.Property(e => e.Status)
                    .IsRequired()
                    .HasMaxLength(50)
                    .HasDefaultValue("Pending");

                entity.Property(e => e.CreatedAt)
                    .IsRequired()
                    .HasDefaultValueSql("CURRENT_TIMESTAMP");

                entity.Property(e => e.UpdatedAt);
            });
        }
    }
}
