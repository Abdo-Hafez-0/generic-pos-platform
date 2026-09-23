using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Inventory.Domain.Entities;
using Inventory.Domain.Enums;
using Inventory.Domain.ValueObjects;

namespace Inventory.Infrastructure.Persistence.Configurations;

internal sealed class StockAdjustmentConfiguration : IEntityTypeConfiguration<StockAdjustment>
{
    public void Configure(EntityTypeBuilder<StockAdjustment> builder)
    {
        builder.ToTable("inv_StockAdjustments");

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id)
            .HasConversion(id => id.Value, value => new StockAdjustmentId(value))
            .IsRequired();

        builder.Property(a => a.StockItemId)
            .HasConversion(id => id.Value, value => new StockItemId(value))
            .IsRequired();

        // AdjustmentQuantity is signed decimal (TEXT for SQLite precision)
        builder.Property(a => a.AdjustmentQuantity)
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(a => a.Reason)
            .HasConversion<int>()
            .IsRequired();

        builder.Property(a => a.Notes)
            .HasMaxLength(500);

        builder.Property(a => a.AdjustedAt).IsRequired();

        builder.HasIndex(a => a.StockItemId);

        // Ignore domain events
        builder.Ignore(a => a.DomainEvents);
    }
}
