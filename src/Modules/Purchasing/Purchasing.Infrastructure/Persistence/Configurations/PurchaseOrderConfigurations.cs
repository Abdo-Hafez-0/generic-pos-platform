using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Purchasing.Domain.Entities;
using Purchasing.Domain.ValueObjects;

namespace Purchasing.Infrastructure.Persistence.Configurations;

/// <summary>PurchaseOrder aggregate root. Table: pur_PurchaseOrders</summary>
internal sealed class PurchaseOrderConfiguration : IEntityTypeConfiguration<PurchaseOrder>
{
    public void Configure(EntityTypeBuilder<PurchaseOrder> builder)
    {
        builder.ToTable("pur_PurchaseOrders");

        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).HasConversion(id => id.Value, v => new PurchaseOrderId(v)).IsRequired();

        builder.Property(o => o.Number).HasMaxLength(40).IsRequired();

        // Plain Guid reference to a Suppliers supplier - no FK, no Suppliers type. Code/name are snapshots.
        builder.Property(o => o.SupplierId).IsRequired();
        builder.Property(o => o.SupplierCode).HasMaxLength(30).IsRequired();
        builder.Property(o => o.SupplierName).HasMaxLength(200).IsRequired();

        builder.Property(o => o.Reference).HasMaxLength(100);
        builder.Property(o => o.Notes).HasMaxLength(1000);
        builder.Property(o => o.Status).HasConversion<int>().IsRequired();

        // Plain Guid reference to an Inventory warehouse - no FK.
        builder.Property(o => o.WarehouseId);

        builder.Property(o => o.TotalAmount)
            .HasConversion(m => m.Amount, v => new Money(v))
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(o => o.ReceivedAmount)
            .HasConversion(m => m.Amount, v => new Money(v))
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(o => o.CreatedAt).IsRequired();
        builder.Property(o => o.UpdatedAt).IsRequired();
        builder.Property(o => o.SubmittedAt);
        builder.Property(o => o.ReceivedAt);
        builder.Property(o => o.CancelledAt);
        builder.Property(o => o.CancellationReason).HasMaxLength(500);
        builder.Property(o => o.ClosedAt);
        builder.Property(o => o.ClosingReason).HasMaxLength(500);
        builder.Ignore(o => o.IsAwaitingGoods);

        builder.HasIndex(o => o.Number).IsUnique();
        builder.HasIndex(o => o.SupplierId);
        builder.HasIndex(o => o.Status);
        builder.HasIndex(o => o.CreatedAt);

        builder.HasMany(o => o.Lines).WithOne().HasForeignKey("PurchaseOrderId").OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Table: pur_PurchaseOrderLines. Product SKU, name and unit cost are snapshots.</summary>
internal sealed class PurchaseOrderLineConfiguration : IEntityTypeConfiguration<PurchaseOrderLine>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderLine> builder)
    {
        builder.ToTable("pur_PurchaseOrderLines");

        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).HasConversion(id => id.Value, v => new PurchaseOrderLineId(v)).IsRequired();
        builder.Property(l => l.PurchaseOrderId).HasConversion(id => id.Value, v => new PurchaseOrderId(v)).IsRequired();

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

        builder.Property(l => l.ReceivedQuantity).HasColumnType("TEXT").IsRequired();
        builder.Property(l => l.ReceivedAt);

        builder.HasIndex(l => l.PurchaseOrderId);
        builder.HasIndex(l => l.ProductId);

        builder.Ignore(l => l.LineTotal);
        builder.Ignore(l => l.IsReceived);
        builder.Ignore(l => l.HasReceipts);
        builder.Ignore(l => l.OutstandingQuantity);
        builder.Ignore(l => l.ReceivedTotal);
    }
}
