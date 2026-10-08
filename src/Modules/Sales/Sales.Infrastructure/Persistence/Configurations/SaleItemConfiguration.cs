using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sales.Domain.Entities;
using Sales.Domain.ValueObjects;

namespace Sales.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core configuration for SaleItem.
/// Table: sal_SaleItems
///
/// HISTORICAL DATA RULE: UnitPrice, Discount, TaxRate are stored as snapshot values.
/// They do not reference Catalog tables — they are permanently fixed at sale time.
/// </summary>
internal sealed class SaleItemConfiguration : IEntityTypeConfiguration<SaleItem>
{
    public void Configure(EntityTypeBuilder<SaleItem> builder)
    {
        builder.ToTable("sal_SaleItems");

        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id)
            .HasConversion(id => id.Value, value => new SaleItemId(value))
            .IsRequired();

        builder.Property(i => i.SaleId)
            .HasConversion(id => id.Value, value => new SaleId(value))
            .IsRequired();

        // CatalogProductId stored as Guid — no reference to Catalog.Domain
        builder.Property(i => i.CatalogProductId).IsRequired();

        // Historical snapshot fields
        builder.Property(i => i.ProductName)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(i => i.ProductSku)
            .HasMaxLength(100)
            .IsRequired();

        // SaleQuantity value object — stored as decimal
        builder.Property(i => i.Quantity)
            .HasConversion(
                q => q.Value,
                value => new SaleQuantity(value))
            .HasColumnType("TEXT")
            .IsRequired();

        // Money value objects — stored as decimal TEXT for SQLite precision
        builder.Property(i => i.UnitPrice)
            .HasConversion(
                m => m.Amount,
                value => new Money(value))
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(i => i.Discount)
            .HasConversion(
                m => m.Amount,
                value => new Money(value))
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(i => i.TaxRate)
            .HasColumnType("TEXT")
            .IsRequired();

        builder.HasIndex(i => i.SaleId);
        builder.HasIndex(i => i.CatalogProductId);

        // Computed properties — derived from stored fields
        builder.Ignore(i => i.Amounts);
        builder.Ignore(i => i.SubTotal);
        builder.Ignore(i => i.TaxAmount);
        builder.Ignore(i => i.LineTotal);
    }
}
