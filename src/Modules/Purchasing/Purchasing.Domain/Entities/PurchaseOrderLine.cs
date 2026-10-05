using Platform.Core.Results;
using Purchasing.Domain.ValueObjects;

namespace Purchasing.Domain.Entities;

/// <summary>
/// One product line of a purchase order. SKU, name and unit cost are snapshots taken when the line is added; the product is
/// referenced only by its Catalog ID.
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

    /// <summary>Set when this line's quantity was received into stock through Inventory.Contracts.</summary>
    public DateTime? ReceivedAt { get; private set; }

    public bool IsReceived => ReceivedAt is not null;
    public Money LineTotal => UnitCost * Quantity.Value;

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

    internal Result MarkReceived()
    {
        if (IsReceived)
            return Result.Failure(Error.Conflict("Purchasing.PurchaseOrder.LineAlreadyReceived", "The line was already received."));

        ReceivedAt = DateTime.UtcNow;
        return Result.Success();
    }
}
