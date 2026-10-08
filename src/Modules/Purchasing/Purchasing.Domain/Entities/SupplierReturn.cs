using Platform.Core.Results;
using Purchasing.Domain.ValueObjects;

namespace Purchasing.Domain.Entities;

/// <summary>
/// Goods sent back to the supplier from a received purchase order (FIX-09b; aggregate root).
///
/// A return belongs to ONE order: its lines name the order lines they come from, at the order's unit cost, out of the warehouse
/// the order was received into. The order itself keeps how much of each line was returned (<see cref="PurchaseOrderLine.ReturnedQuantity"/>),
/// so a line can never be returned beyond what was received. Supplier, order number and product details are snapshots.
/// The stock leaves through Inventory.Contracts; this record says why and what.
/// </summary>
public sealed class SupplierReturn
{
    private readonly List<SupplierReturnLine> _lines = [];

    private SupplierReturn() { }

    public SupplierReturnId Id { get; private set; }

    /// <summary>Human-readable return number, e.g. RT-20261008-A1B2C3.</summary>
    public string Number { get; private set; } = string.Empty;

    public PurchaseOrderId PurchaseOrderId { get; private set; }
    public string PurchaseOrderNumber { get; private set; } = string.Empty;
    public Guid SupplierId { get; private set; }
    public string SupplierName { get; private set; } = string.Empty;

    /// <summary>The warehouse the goods leave (the order's receiving warehouse). Plain Guid reference to an Inventory warehouse.</summary>
    public Guid WarehouseId { get; private set; }

    public string Reason { get; private set; } = string.Empty;
    public Money TotalAmount { get; private set; }
    public DateTime CreatedAt { get; private set; }

    public IReadOnlyList<SupplierReturnLine> Lines => _lines.AsReadOnly();

    /// <summary>Starts a return for an order that has received goods. Lines are added with <see cref="AddLine"/>.</summary>
    public static Result<SupplierReturn> Start(PurchaseOrder order, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure<SupplierReturn>(Error.Validation("Purchasing.SupplierReturn.ReasonRequired", "A reason is required to return goods to the supplier."));
        if (reason.Trim().Length > 500)
            return Result.Failure<SupplierReturn>(Error.Validation("Purchasing.SupplierReturn.ReasonTooLong", "The reason cannot exceed 500 characters."));
        if (order.WarehouseId is not { } warehouse || !order.Lines.Any(l => l.HasReceipts))
            return Result.Failure<SupplierReturn>(Error.Conflict("Purchasing.SupplierReturn.NothingReceived",
                $"Nothing of order {order.Number} was received yet, so nothing can be returned."));

        var now = DateTime.UtcNow;
        return Result.Success(new SupplierReturn
        {
            Id = SupplierReturnId.New(),
            Number = $"RT-{now:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}",
            PurchaseOrderId = order.Id,
            PurchaseOrderNumber = order.Number,
            SupplierId = order.SupplierId,
            SupplierName = order.SupplierName,
            WarehouseId = warehouse,
            Reason = reason.Trim(),
            TotalAmount = Money.Zero,
            CreatedAt = now
        });
    }

    /// <summary>Records the line on the return; the order line must take the return first (<see cref="PurchaseOrder.RecordReturn"/>).</summary>
    internal void AddLine(PurchaseOrderLine from, OrderQuantity quantity)
    {
        _lines.Add(SupplierReturnLine.Create(Id, from, quantity));
        TotalAmount = _lines.Aggregate(Money.Zero, (acc, l) => acc + l.LineTotal);
    }
}

/// <summary>One product line of a supplier return: which order line it comes from, how much, at the order's unit cost.</summary>
public sealed class SupplierReturnLine
{
    private SupplierReturnLine() { }

    public SupplierReturnLineId Id { get; private set; }
    public SupplierReturnId SupplierReturnId { get; private set; }
    public PurchaseOrderLineId PurchaseOrderLineId { get; private set; }
    public Guid ProductId { get; private set; }
    public string ProductSku { get; private set; } = string.Empty;
    public string ProductName { get; private set; } = string.Empty;
    public OrderQuantity Quantity { get; private set; }
    public Money UnitCost { get; private set; }

    public Money LineTotal => UnitCost * Quantity.Value;

    internal static SupplierReturnLine Create(SupplierReturnId returnId, PurchaseOrderLine from, OrderQuantity quantity) => new()
    {
        Id = SupplierReturnLineId.New(),
        SupplierReturnId = returnId,
        PurchaseOrderLineId = from.Id,
        ProductId = from.ProductId,
        ProductSku = from.ProductSku,
        ProductName = from.ProductName,
        Quantity = quantity,
        UnitCost = from.UnitCost
    };
}
