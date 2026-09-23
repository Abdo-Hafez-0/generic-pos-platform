using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Inventory.Domain.Entities;
using Inventory.Domain.ValueObjects;

namespace Inventory.Infrastructure.Persistence.Configurations;

internal sealed class LocationConfiguration : IEntityTypeConfiguration<Location>
{
    public void Configure(EntityTypeBuilder<Location> builder)
    {
        builder.ToTable("inv_Locations");

        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id)
            .HasConversion(id => id.Value, value => new LocationId(value))
            .IsRequired();

        builder.Property(l => l.WarehouseId)
            .HasConversion(id => id.Value, value => new WarehouseId(value))
            .IsRequired();

        builder.Property(l => l.Name)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(l => l.Code)
            .HasMaxLength(20)
            .IsRequired();

        // Code unique within a warehouse
        builder.HasIndex(l => new { l.WarehouseId, l.Code }).IsUnique();

        builder.Property(l => l.Description)
            .HasMaxLength(500);

        builder.Property(l => l.IsActive).IsRequired();
        builder.Property(l => l.CreatedAt).IsRequired();
        builder.Property(l => l.UpdatedAt).IsRequired();

        // Ignore domain events
        builder.Ignore(l => l.DomainEvents);
    }
}
