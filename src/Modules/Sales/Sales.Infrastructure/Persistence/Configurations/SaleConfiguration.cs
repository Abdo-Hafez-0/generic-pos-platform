using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sales.Domain.Entities;
using Sales.Domain.Enums;
using Sales.Domain.ValueObjects;

namespace Sales.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core configuration for the Sale aggregate root.
/// Table: sal_Sales
/// </summary>
internal sealed class SaleConfiguration : IEntityTypeConfiguration<Sale>
{
    public void Configure(EntityTypeBuilder<Sale> builder)
    {
        builder.ToTable("sal_Sales");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id)
            .HasConversion(id => id.Value, value => new SaleId(value))
            .IsRequired();

        builder.Property(s => s.Status)
            .HasConversion<int>()
            .IsRequired();

        builder.Property(s => s.Reference)
            .HasMaxLength(100);

        builder.Property(s => s.Notes)
            .HasMaxLength(500);

        // FIX-11: plain Guid reference to a Customers customer (no FK) and the code/name snapshot
        builder.Property(s => s.CustomerId);
        builder.Property(s => s.CustomerCode).HasMaxLength(30);
        builder.Property(s => s.CustomerName).HasMaxLength(200);
        builder.HasIndex(s => s.CustomerId);

        builder.Property(s => s.CancellationReason)
            .HasMaxLength(500);

        builder.Property(s => s.CreatedAt).IsRequired();
        builder.Property(s => s.UpdatedAt).IsRequired();
        builder.Property(s => s.CompletedAt);
        builder.Property(s => s.CancelledAt);

        // Status index for filtering
        builder.HasIndex(s => s.Status);
        builder.HasIndex(s => s.CreatedAt);
        builder.HasIndex(s => s.CompletedAt);   // FIX-12: reports read completed sales by range

        // Owned items collection — separate table (sal_SaleItems)
        // Note: no HasPrincipalKey — EF resolves via the configured Id property conversion
        builder.HasMany(s => s.Items)
            .WithOne()
            .HasForeignKey("SaleId")
            .OnDelete(DeleteBehavior.Cascade);

        // Ignore domain events — not persisted
        builder.Ignore(s => s.DomainEvents);

        // Ignore computed properties — derived from items
        builder.Ignore(s => s.SubTotal);
        builder.Ignore(s => s.TaxTotal);
        builder.Ignore(s => s.GrandTotal);
    }
}
