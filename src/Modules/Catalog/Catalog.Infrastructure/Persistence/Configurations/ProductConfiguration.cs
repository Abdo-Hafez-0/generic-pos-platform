using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Catalog.Domain.Entities;
using Catalog.Domain.ValueObjects;
using Catalog.Domain.Enums;

namespace Catalog.Infrastructure.Persistence.Configurations;

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("cat_Products");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id)
            .HasConversion(id => id.Value, value => new ProductId(value))
            .IsRequired();

        builder.Property(p => p.Sku)
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(p => p.Name)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(p => p.Description)
            .HasMaxLength(1000);

        builder.Property(p => p.CategoryId)
            .HasConversion(id => id.Value, value => new CategoryId(value))
            .IsRequired();

        builder.Property(p => p.UnitId)
            .HasConversion(id => id.Value, value => new UnitId(value))
            .IsRequired();

        builder.Property(p => p.Status)
            .HasConversion<int>()
            .IsRequired();

        builder.Property(p => p.SalePrice)
            .HasColumnType("TEXT")  // SQLite stores decimal as TEXT for precision
            .IsRequired();

        builder.Property(p => p.CostPrice)
            .HasColumnType("TEXT");

        builder.Property(p => p.CreatedAt).IsRequired();
        builder.Property(p => p.UpdatedAt).IsRequired();

        // Unique index on SKU
        builder.HasIndex(p => p.Sku).IsUnique();

        // Navigation to Barcodes
        builder.HasMany(p => p.Barcodes)
            .WithOne()
            .HasForeignKey(b => b.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        // Ignore domain events collection (not persisted)
        builder.Ignore(p => p.DomainEvents);
    }
}
