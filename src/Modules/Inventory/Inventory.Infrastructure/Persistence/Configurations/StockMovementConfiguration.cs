using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Inventory.Domain.Entities;
using Inventory.Domain.Enums;
using Inventory.Domain.ValueObjects;

namespace Inventory.Infrastructure.Persistence.Configurations;

internal sealed class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> builder)
    {
        builder.ToTable("inv_StockMovements");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id)
            .HasConversion(id => id.Value, value => new StockMovementId(value))
            .IsRequired();

        builder.Property(m => m.StockItemId)
            .HasConversion(id => id.Value, value => new StockItemId(value))
            .IsRequired();

        builder.Property(m => m.MovementType)
            .HasConversion<int>()
            .IsRequired();

        // Quantity value object — stored as decimal TEXT for SQLite precision
        builder.Property(m => m.Quantity)
            .HasConversion(
                q => q.Value,
                value => Quantity.FromTrusted(value))
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(m => m.Reference)
            .HasMaxLength(200);

        builder.Property(m => m.OccurredAt).IsRequired();

        // Index for efficient movement history retrieval
        builder.HasIndex(m => m.StockItemId);
        builder.HasIndex(m => m.OccurredAt);

        // Ignore domain events
        builder.Ignore(m => m.DomainEvents);
    }
}
