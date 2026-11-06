using Microsoft.EntityFrameworkCore;
using OrderIntakeApi.Models;

namespace OrderIntakeApi.Data;

public class OrderDbContext : DbContext
{
    public OrderDbContext(DbContextOptions<OrderDbContext> options) : base(options)
    {
    }

    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasKey(o => o.Id);

            entity.HasIndex(o => o.IdempotencyKey)
                .IsUnique();

            entity.HasIndex(o => new { o.CustomerId, o.CreatedAtUtc });

            entity.Property(o => o.PaymentStatus)
                .HasConversion<string>()
                .HasMaxLength(32);

            entity.Property(o => o.TotalAmount)
                .HasPrecision(18, 2);

            entity.Property(o => o.Version)
                .IsConcurrencyToken();

            entity.HasMany(o => o.Items)
                .WithOne(i => i.Order)
                .HasForeignKey(i => i.OrderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OrderItem>(entity =>
        {
            entity.HasKey(i => i.Id);

            entity.Property(i => i.UnitPrice)
                .HasPrecision(18, 2);
        });
    }
}
