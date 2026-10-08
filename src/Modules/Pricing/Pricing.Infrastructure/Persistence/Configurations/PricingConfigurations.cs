using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pricing.Domain.Entities;
using Pricing.Domain.ValueObjects;

namespace Pricing.Infrastructure.Persistence.Configurations;

/// <summary>Table: pri_PriceLists</summary>
internal sealed class PriceListConfiguration : IEntityTypeConfiguration<PriceList>
{
    public void Configure(EntityTypeBuilder<PriceList> builder)
    {
        builder.ToTable("pri_PriceLists");

        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).HasConversion(id => id.Value, v => new PriceListId(v)).IsRequired();

        builder.Property(l => l.Code).HasMaxLength(30).IsRequired();
        builder.Property(l => l.Name).HasMaxLength(200).IsRequired();
        builder.Property(l => l.IsDefault).IsRequired();
        builder.Property(l => l.Status).HasConversion<int>().IsRequired();
        builder.Property(l => l.CreatedAt).IsRequired();

        builder.HasIndex(l => l.Code).IsUnique();
        builder.HasIndex(l => l.IsDefault);
    }
}

/// <summary>Table: pri_Prices. The product is a plain Catalog ID (no FK, no Catalog type).</summary>
internal sealed class PriceConfiguration : IEntityTypeConfiguration<Price>
{
    public void Configure(EntityTypeBuilder<Price> builder)
    {
        builder.ToTable("pri_Prices");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasConversion(id => id.Value, v => new PriceId(v)).IsRequired();
        builder.Property(p => p.PriceListId).HasConversion(id => id.Value, v => new PriceListId(v)).IsRequired();
        builder.Property(p => p.ProductId).IsRequired();

        builder.Property(p => p.Amount)
            .HasConversion(m => m.Amount, v => new Money(v))
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(p => p.MinimumQuantity).HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.EffectiveFrom).IsRequired();
        builder.Property(p => p.EffectiveTo);
        builder.Property(p => p.Status).HasConversion<int>().IsRequired();
        builder.Property(p => p.CreatedAt).IsRequired();
        builder.Property(p => p.UpdatedAt).IsRequired();

        builder.HasIndex(p => new { p.ProductId, p.PriceListId });
        builder.HasIndex(p => p.Status);

        // Same-module relationship only (a price belongs to a price list of THIS module).
        builder.HasOne<PriceList>().WithMany().HasForeignKey(p => p.PriceListId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Table: pri_TaxRates (FIX-08a).</summary>
internal sealed class TaxRateConfiguration : IEntityTypeConfiguration<TaxRate>
{
    public void Configure(EntityTypeBuilder<TaxRate> builder)
    {
        builder.ToTable("pri_TaxRates");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasConversion(id => id.Value, v => new TaxRateId(v)).IsRequired();
        builder.Property(r => r.Code).HasMaxLength(30).IsRequired();
        builder.Property(r => r.Name).HasMaxLength(200).IsRequired();
        builder.Property(r => r.Rate).HasColumnType("TEXT").IsRequired();
        builder.Property(r => r.IsDefault).IsRequired();
        builder.Property(r => r.Status).HasConversion<int>().IsRequired();
        builder.Property(r => r.CreatedAt).IsRequired();
        builder.Property(r => r.UpdatedAt).IsRequired();

        builder.HasIndex(r => r.Code).IsUnique();
        builder.HasIndex(r => r.IsDefault);
    }
}

/// <summary>Table: pri_ProductTaxRates (FIX-08a). The product is a plain Catalog ID (no FK, no Catalog type); one row per product.</summary>
internal sealed class ProductTaxRateConfiguration : IEntityTypeConfiguration<ProductTaxRate>
{
    public void Configure(EntityTypeBuilder<ProductTaxRate> builder)
    {
        builder.ToTable("pri_ProductTaxRates");

        builder.HasKey(a => a.ProductId);
        builder.Property(a => a.TaxRateId).HasConversion(id => id.Value, v => new TaxRateId(v)).IsRequired();
        builder.Property(a => a.UpdatedAt).IsRequired();
        builder.HasIndex(a => a.TaxRateId);

        // Same-module relationship only (an assignment points at a tax rate of THIS module).
        builder.HasOne<TaxRate>().WithMany().HasForeignKey(a => a.TaxRateId).OnDelete(DeleteBehavior.Restrict);
    }
}
