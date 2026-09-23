using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Catalog.Domain.Entities;
using Catalog.Domain.ValueObjects;
using Catalog.Domain.Enums;

namespace Catalog.Infrastructure.Persistence.Configurations;

internal sealed class BarcodeConfiguration : IEntityTypeConfiguration<Barcode>
{
    public void Configure(EntityTypeBuilder<Barcode> builder)
    {
        builder.ToTable("cat_Barcodes");

        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id)
            .HasConversion(id => id.Value, value => new BarcodeId(value))
            .IsRequired();

        builder.Property(b => b.ProductId)
            .HasConversion(id => id.Value, value => new ProductId(value))
            .IsRequired();

        builder.Property(b => b.Value)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(b => b.Format)
            .HasConversion<int>()
            .IsRequired();

        builder.Property(b => b.CreatedAt).IsRequired();

        // Index for fast barcode lookup (key scenario: POS barcode scan)
        builder.HasIndex(b => b.Value);
    }
}
