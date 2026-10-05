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
