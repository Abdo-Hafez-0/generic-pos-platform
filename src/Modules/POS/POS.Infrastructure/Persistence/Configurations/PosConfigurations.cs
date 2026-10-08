using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using POS.Domain.Entities;
using POS.Domain.ValueObjects;

namespace POS.Infrastructure.Persistence.Configurations;

/// <summary>EF Core configuration for PosSession. Table: pos_Sessions</summary>
internal sealed class PosSessionConfiguration : IEntityTypeConfiguration<PosSession>
{
    public void Configure(EntityTypeBuilder<PosSession> builder)
    {
        builder.ToTable("pos_Sessions");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id)
            .HasConversion(id => id.Value, value => new PosSessionId(value))
            .IsRequired();

        builder.Property(s => s.CashierReference).HasMaxLength(100).IsRequired();

        // Plain Guid reference to an Inventory warehouse - no foreign key, no Inventory type.
        builder.Property(s => s.WarehouseId).IsRequired();

        builder.Property(s => s.Status).HasConversion<int>().IsRequired();
        builder.Property(s => s.OpenedAt).IsRequired();
        builder.Property(s => s.ClosedAt);

        builder.HasIndex(s => s.Status);
    }
}

/// <summary>EF Core configuration for the PosCart aggregate root. Table: pos_Carts</summary>
internal sealed class PosCartConfiguration : IEntityTypeConfiguration<PosCart>
{
    public void Configure(EntityTypeBuilder<PosCart> builder)
    {
        builder.ToTable("pos_Carts");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id)
            .HasConversion(id => id.Value, value => new PosCartId(value))
            .IsRequired();

        // Reference to pos_Sessions by value; no FK constraint is declared in the model.
        builder.Property(c => c.SessionId)
            .HasConversion(id => id.Value, value => new PosSessionId(value))
            .IsRequired();

        builder.Property(c => c.Status).HasConversion<int>().IsRequired();
        builder.Property(c => c.CreatedAt).IsRequired();
        builder.Property(c => c.UpdatedAt).IsRequired();
        builder.Property(c => c.CheckedOutAt);

        // Plain Guid reference to the Sales module sale - no FK, no Sales type.
        builder.Property(c => c.SaleId);

        builder.HasIndex(c => c.SessionId);
        builder.HasIndex(c => c.Status);

        builder.HasMany(c => c.Items)
            .WithOne()
            .HasForeignKey("CartId")
            .OnDelete(DeleteBehavior.Cascade);

        // Computed properties - derived from items
        builder.Ignore(c => c.Subtotal);
        builder.Ignore(c => c.Total);
        builder.Ignore(c => c.TotalQuantity);
    }
}

/// <summary>
/// EF Core configuration for PosCartItem. Table: pos_CartItems
/// ProductSku, ProductName and UnitPrice are stored as snapshots taken when the line was added.
/// </summary>
internal sealed class PosCartItemConfiguration : IEntityTypeConfiguration<PosCartItem>
{
    public void Configure(EntityTypeBuilder<PosCartItem> builder)
    {
        builder.ToTable("pos_CartItems");

        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id)
            .HasConversion(id => id.Value, value => new PosCartItemId(value))
            .IsRequired();

        builder.Property(i => i.CartId)
            .HasConversion(id => id.Value, value => new PosCartId(value))
            .IsRequired();

        // Plain Guid reference to a Catalog product - no FK, no Catalog type.
        builder.Property(i => i.CatalogProductId).IsRequired();

        builder.Property(i => i.ProductSku).HasMaxLength(100).IsRequired();
        builder.Property(i => i.ProductName).HasMaxLength(200).IsRequired();

        builder.Property(i => i.Quantity)
            .HasConversion(q => q.Value, value => new CartQuantity(value))
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(i => i.UnitPrice)
            .HasConversion(m => m.Amount, value => new Money(value))
            .HasColumnType("TEXT")
            .IsRequired();

        // FIX-08b: the tax rate snapshot (prices include tax)
        builder.Property(i => i.TaxRate).HasColumnType("TEXT").IsRequired().HasDefaultValue(0m);

        builder.HasIndex(i => i.CartId);
        builder.HasIndex(i => i.CatalogProductId);

        builder.Ignore(i => i.Amounts);
        builder.Ignore(i => i.TaxAmount);
        builder.Ignore(i => i.LineTotal);
    }
}
