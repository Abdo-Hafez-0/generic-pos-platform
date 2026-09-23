using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Inventory.Domain.Entities;
using Inventory.Domain.ValueObjects;

namespace Inventory.Infrastructure.Persistence.Configurations;

internal sealed class InventoryBalanceConfiguration : IEntityTypeConfiguration<InventoryBalance>
{
    public void Configure(EntityTypeBuilder<InventoryBalance> builder)
    {
        builder.ToTable("inv_InventoryBalances");

        // StockItemId is both the primary key and the foreign relationship to inv_StockItems.
        // 1-to-1 relationship: one balance per stock item.
        builder.HasKey(b => b.StockItemId);
        builder.Property(b => b.StockItemId)
            .HasConversion(id => id.Value, value => new StockItemId(value))
            .IsRequired();

        // OnHand is the Quantity value object (stored as TEXT for decimal precision)
        builder.Property(b => b.OnHand)
            .HasConversion(
                q => q.Value,
                value => Quantity.FromTrusted(value))
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(b => b.UpdatedAt).IsRequired();
    }
}
