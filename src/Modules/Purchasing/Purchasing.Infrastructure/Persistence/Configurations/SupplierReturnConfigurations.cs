using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Purchasing.Domain.Entities;
using Purchasing.Domain.ValueObjects;

namespace Purchasing.Infrastructure.Persistence.Configurations;

/// <summary>SupplierReturn aggregate root (FIX-09b). Table: pur_SupplierReturns. Order number, supplier and products are snapshots.</summary>
internal sealed class SupplierReturnConfiguration : IEntityTypeConfiguration<SupplierReturn>
{
    public void Configure(EntityTypeBuilder<SupplierReturn> builder)
    {
        builder.ToTable("pur_SupplierReturns");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasConversion(id => id.Value, v => new SupplierReturnId(v)).IsRequired();
        builder.Property(r => r.Number).HasMaxLength(40).IsRequired();

        // same-module reference to the order; the return outlives nothing it does not own
        builder.Property(r => r.PurchaseOrderId).HasConversion(id => id.Value, v => new PurchaseOrderId(v)).IsRequired();
        builder.HasOne<PurchaseOrder>().WithMany().HasForeignKey(r => r.PurchaseOrderId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(r => r.PurchaseOrderNumber).HasMaxLength(40).IsRequired();

        // Plain Guid references to a Suppliers supplier and an Inventory warehouse - no FK.
        builder.Property(r => r.SupplierId).IsRequired();
        builder.Property(r => r.SupplierName).HasMaxLength(200).IsRequired();
        builder.Property(r => r.WarehouseId).IsRequired();

        builder.Property(r => r.Reason).HasMaxLength(500).IsRequired();
        builder.Property(r => r.TotalAmount)
            .HasConversion(m => m.Amount, v => new Money(v))
            .HasColumnType("TEXT")
            .IsRequired();
        builder.Property(r => r.CreatedAt).IsRequired();

        builder.HasIndex(r => r.Number).IsUnique();
        builder.HasIndex(r => r.PurchaseOrderId);
        builder.HasIndex(r => r.CreatedAt);

        builder.HasMany(r => r.Lines).WithOne().HasForeignKey(l => l.SupplierReturnId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Table: pur_SupplierReturnLines.</summary>
internal sealed class SupplierReturnLineConfiguration : IEntityTypeConfiguration<SupplierReturnLine>
{
    public void Configure(EntityTypeBuilder<SupplierReturnLine> builder)
    {
        builder.ToTable("pur_SupplierReturnLines");

        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).HasConversion(id => id.Value, v => new SupplierReturnLineId(v)).IsRequired();
        builder.Property(l => l.SupplierReturnId).HasConversion(id => id.Value, v => new SupplierReturnId(v)).IsRequired();
        builder.Property(l => l.PurchaseOrderLineId).HasConversion(id => id.Value, v => new PurchaseOrderLineId(v)).IsRequired();

        // Plain Guid reference to a Catalog product - no FK, no Catalog type.
        builder.Property(l => l.ProductId).IsRequired();
        builder.Property(l => l.ProductSku).HasMaxLength(100).IsRequired();
        builder.Property(l => l.ProductName).HasMaxLength(200).IsRequired();

        builder.Property(l => l.Quantity)
            .HasConversion(q => q.Value, v => new OrderQuantity(v))
            .HasColumnType("TEXT")
            .IsRequired();
        builder.Property(l => l.UnitCost)
            .HasConversion(m => m.Amount, v => new Money(v))
            .HasColumnType("TEXT")
            .IsRequired();

        builder.HasIndex(l => l.SupplierReturnId);
        builder.Ignore(l => l.LineTotal);
    }
}
