using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Inventory.Domain.Entities;
using Inventory.Domain.ValueObjects;

namespace Inventory.Infrastructure.Persistence.Configurations;

internal sealed class StockItemConfiguration : IEntityTypeConfiguration<StockItem>
{
    public void Configure(EntityTypeBuilder<StockItem> builder)
    {
        builder.ToTable("inv_StockItems");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id)
            .HasConversion(id => id.Value, value => new StockItemId(value))
            .IsRequired();

        // CatalogProductId is stored as Guid — no reference to Catalog.Domain types.
        builder.Property(s => s.CatalogProductId).IsRequired();

        builder.Property(s => s.WarehouseId)
            .HasConversion(id => id.Value, value => new WarehouseId(value))
            .IsRequired();

        // LocationId is nullable (warehouse-level tracking)
        builder.Property(s => s.LocationId)
            .HasConversion(
                id => id.HasValue ? id.Value.Value : (Guid?)null,
                value => value.HasValue ? new LocationId(value.Value) : (LocationId?)null);

        // Unique index: one StockItem per (product, warehouse) combination
        builder.HasIndex(s => new { s.CatalogProductId, s.WarehouseId }).IsUnique();

        builder.Property(s => s.IsActive).IsRequired();
        builder.Property(s => s.CreatedAt).IsRequired();
        builder.Property(s => s.UpdatedAt).IsRequired();

        // Ignore domain events
        builder.Ignore(s => s.DomainEvents);
    }
}
