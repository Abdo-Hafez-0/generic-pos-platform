using System.Globalization;
using Platform.Core.Results;
using Purchasing.Domain.ValueObjects;

namespace Purchasing.Domain.Entities;

/// <summary>
/// One product line of a purchase order. SKU, name and unit cost are snapshots taken when the line is added; the product is
/// referenced only by its Catalog ID.
///
/// FIX-09: goods arrive in one or more deliveries; <see cref="ReceivedQuantity"/> adds them up and never exceeds what was ordered.
/// </summary>
public sealed class PurchaseOrderLine
{
    private PurchaseOrderLine() { }

    public PurchaseOrderLineId Id { get; private set; }
    public PurchaseOrderId PurchaseOrderId { get; private set; }
    public Guid ProductId { get; private set; }
    public string ProductSku { get; private set; } = string.Empty;
    public string ProductName { get; private set; } = string.Empty;
    public OrderQuantity Quantity { get; private set; }
    public Money UnitCost { get; private set; }

    /// <summary>How much of the line was received into stock so far, over every delivery (0..<see cref="Quantity"/>).</summary>
    public decimal ReceivedQuantity { get; private set; }

    /// <summary>Set when the line's whole quantity has been received into stock through Inventory.Contracts.</summary>
    public DateTime? ReceivedAt { get; private set; }

    public bool IsReceived => ReceivedQuantity >= Quantity.Value;
    public bool HasReceipts => ReceivedQuantity > 0m;
    public decimal OutstandingQuantity => Math.Max(0m, Quantity.Value - ReceivedQuantity);
    public Money LineTotal => UnitCost * Quantity.Value;
    public Money ReceivedTotal => UnitCost * ReceivedQuantity;

    internal static Result<PurchaseOrderLine> Create(
        PurchaseOrderId orderId, Guid productId, string sku, string name, OrderQuantity quantity, Money unitCost)
    {
        if (productId == Guid.Empty)
            return Result.Failure<PurchaseOrderLine>(Error.Validation("Purchasing.PurchaseOrder.ProductRequired", "A product is required."));
        if (string.IsNullOrWhiteSpace(sku) || string.IsNullOrWhiteSpace(name))
            return Result.Failure<PurchaseOrderLine>(Error.Validation("Purchasing.PurchaseOrder.ProductSnapshotRequired", "Product SKU and name are required for the order record."));

        return Result.Success(new PurchaseOrderLine
        {
            Id = PurchaseOrderLineId.New(),
            PurchaseOrderId = orderId,
            ProductId = productId,
            ProductSku = sku.Trim(),
            ProductName = name.Trim(),
            Quantity = quantity,
            UnitCost = unitCost
        });
    }

    internal void Set(OrderQuantity quantity, Money unitCost)
    {
        Quantity = quantity;
        UnitCost = unitCost;
    }

    /// <summary>Records a delivery of this line. More than is still outstanding is refused (nothing changes).</summary>
    internal Result Receive(OrderQuantity quantity)
    {
        if (IsReceived)
            return Result.Failure(Error.Conflict("Purchasing.PurchaseOrder.LineAlreadyReceived", $"'{ProductName}' was already received in full."));

        if (quantity.Value > OutstandingQuantity)
            return Result.Failure(Error.Conflict("Purchasing.PurchaseOrder.MoreThanOrdered",
                $"Only {OutstandingQuantity.ToString("0.###", CultureInfo.CurrentCulture)} of '{ProductName}' are still expected; " +
                $"{quantity.Value.ToString("0.###", CultureInfo.CurrentCulture)} cannot be received."));

        ReceivedQuantity += quantity.Value;
        if (IsReceived) ReceivedAt = DateTime.UtcNow;
        return Result.Success();
    }
}
