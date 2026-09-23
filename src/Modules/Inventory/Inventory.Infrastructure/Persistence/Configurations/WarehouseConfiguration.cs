using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Inventory.Domain.Entities;
using Inventory.Domain.ValueObjects;

namespace Inventory.Infrastructure.Persistence.Configurations;

internal sealed class WarehouseConfiguration : IEntityTypeConfiguration<Warehouse>
{
    public void Configure(EntityTypeBuilder<Warehouse> builder)
    {
        builder.ToTable("inv_Warehouses");

        builder.HasKey(w => w.Id);
        builder.Property(w => w.Id)
            .HasConversion(id => id.Value, value => new WarehouseId(value))
            .IsRequired();

        builder.Property(w => w.Name)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(w => w.Code)
            .HasMaxLength(20)
            .IsRequired();

        builder.HasIndex(w => w.Code).IsUnique();

        builder.Property(w => w.Description)
            .HasMaxLength(500);

        builder.Property(w => w.IsActive).IsRequired();
        builder.Property(w => w.CreatedAt).IsRequired();
        builder.Property(w => w.UpdatedAt).IsRequired();

        // Ignore domain events (not persisted)
        builder.Ignore(w => w.DomainEvents);
    }
}
