using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sales.Domain.Entities;
using Sales.Domain.ValueObjects;

namespace Sales.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core configuration for ReturnItem.
/// Table: sal_ReturnItems
/// </summary>
internal sealed class ReturnItemConfiguration : IEntityTypeConfiguration<ReturnItem>
{
    public void Configure(EntityTypeBuilder<ReturnItem> builder)
    {
        builder.ToTable("sal_ReturnItems");

        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id)
            .HasConversion(id => id.Value, value => new ReturnItemId(value))
            .IsRequired();

        builder.Property(i => i.ReturnId)
            .HasConversion(id => id.Value, value => new ReturnId(value))
            .IsRequired();

        builder.Property(i => i.OriginalSaleItemId)
            .HasConversion(id => id.Value, value => new SaleItemId(value))
            .IsRequired();

        builder.Property(i => i.CatalogProductId).IsRequired();

        builder.Property(i => i.ProductName)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(i => i.Quantity)
            .HasConversion(
                q => q.Value,
                value => new SaleQuantity(value))
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(i => i.UnitPrice)
            .HasConversion(
                m => m.Amount,
                value => new Money(value))
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(i => i.Reason)
            .HasMaxLength(500);

        builder.HasIndex(i => i.ReturnId);
        builder.HasIndex(i => i.OriginalSaleItemId);
    }
}
