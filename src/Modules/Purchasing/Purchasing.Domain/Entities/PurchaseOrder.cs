using Platform.Core.Results;
using Purchasing.Domain.Enums;
using Purchasing.Domain.ValueObjects;

namespace Purchasing.Domain.Entities;

/// <summary>
/// A purchase order (aggregate root).
///
/// OWNERSHIP: Purchasing owns the order and its lifecycle. The supplier, the products and the stock belong to other modules and
/// are referenced by plain Guid IDs plus SNAPSHOTS (supplier code/name, product SKU/name, unit cost) taken when the order is
/// built, so later changes in those modules never rewrite history. Inventory stays the only owner of stock: receiving is done
/// line by line through Inventory.Contracts; the order only records which lines were received.
/// </summary>
public sealed class PurchaseOrder
{
    private readonly List<PurchaseOrderLine> _lines = [];

    private PurchaseOrder() { }

    public PurchaseOrderId Id { get; private set; }

    /// <summary>Human-readable order number, e.g. PO-20261005-A1B2C3.</summary>
    public string Number { get; private set; } = string.Empty;

    public Guid SupplierId { get; private set; }
    public string SupplierCode { get; private set; } = string.Empty;
    public string SupplierName { get; private set; } = string.Empty;

    public string? Reference { get; private set; }
    public string? Notes { get; private set; }
    public PurchaseOrderStatus Status { get; private set; }

    /// <summary>Warehouse the goods are received into (set when receiving starts). Plain Guid reference to an Inventory warehouse.</summary>
    public Guid? WarehouseId { get; private set; }

    /// <summary>Sum of line totals, maintained by the aggregate (so lists/summaries never need the lines).</summary>
    public Money TotalAmount { get; private set; }

    /// <summary>Value of the goods received so far (received quantity x unit cost over the lines), maintained like <see cref="TotalAmount"/>.</summary>
    public Money ReceivedAmount { get; private set; }

    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public DateTime? SubmittedAt { get; private set; }
    public DateTime? ReceivedAt { get; private set; }
    public DateTime? CancelledAt { get; private set; }
    public string? CancellationReason { get; private set; }

    /// <summary>When a partly received order was closed short, and why (FIX-09).</summary>
    public DateTime? ClosedAt { get; private set; }
    public string? ClosingReason { get; private set; }

    /// <summary>True while goods are still expected: Submitted or PartiallyReceived.</summary>
    public bool IsAwaitingGoods => Status is PurchaseOrderStatus.Submitted or PurchaseOrderStatus.PartiallyReceived;

    public IReadOnlyList<PurchaseOrderLine> Lines => _lines.AsReadOnly();

    public static Result<PurchaseOrder> Create(Guid supplierId, string supplierCode, string supplierName, string? reference = null, string? notes = null)
    {
        if (supplierId == Guid.Empty)
            return Result.Failure<PurchaseOrder>(Error.Validation("Purchasing.PurchaseOrder.SupplierRequired", "A supplier is required."));
        if (string.IsNullOrWhiteSpace(supplierCode) || string.IsNullOrWhiteSpace(supplierName))
            return Result.Failure<PurchaseOrder>(Error.Validation("Purchasing.PurchaseOrder.SupplierSnapshotRequired", "Supplier code and name are required for the order record."));
        if (reference is not null && reference.Trim().Length > 100)
            return Result.Failure<PurchaseOrder>(Error.Validation("Purchasing.PurchaseOrder.ReferenceTooLong", "The reference cannot exceed 100 characters."));
        if (notes is not null && notes.Length > 1000)
            return Result.Failure<PurchaseOrder>(Error.Validation("Purchasing.PurchaseOrder.NotesTooLong", "Notes cannot exceed 1000 characters."));

        var now = DateTime.UtcNow;
        return Result.Success(new PurchaseOrder
        {
            Id = PurchaseOrderId.New(),
            Number = $"PO-{now:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}",
            SupplierId = supplierId,
            SupplierCode = supplierCode.Trim(),
            SupplierName = supplierName.Trim(),
            Reference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim(),
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            Status = PurchaseOrderStatus.Draft,
            TotalAmount = Money.Zero,
            ReceivedAmount = Money.Zero,
            CreatedAt = now,
            UpdatedAt = now
        });
    }

    // ------------------------------------------------------------------ lines (Draft only)

    public PurchaseOrderLine? FindLine(PurchaseOrderLineId id) => _lines.FirstOrDefault(l => l.Id == id);

    /// <summary>Adds a line. Adding a product that is already on the order increases that line's quantity and updates its cost.</summary>
    public Result<PurchaseOrderLine> AddLine(Guid productId, string sku, string productName, OrderQuantity quantity, Money unitCost)
    {
        var draft = EnsureDraft<PurchaseOrderLine>();
        if (draft.IsFailure) return draft;

        var existing = _lines.FirstOrDefault(l => l.ProductId == productId);
        if (existing is not null)
        {
            existing.Set(new OrderQuantity(existing.Quantity.Value + quantity.Value), unitCost);
            Recalculate();
            return Result.Success(existing);
        }

        var created = PurchaseOrderLine.Create(Id, productId, sku, productName, quantity, unitCost);
        if (created.IsFailure) return created;

        _lines.Add(created.Value);
        Recalculate();
        return created;
    }

    public Result RemoveLine(PurchaseOrderLineId lineId)
    {
        var draft = EnsureDraft();
        if (draft.IsFailure) return draft;

        var line = FindLine(lineId);
        if (line is null)
            return Result.Failure(Error.NotFound("Purchasing.PurchaseOrder.LineNotFound", "The line was not found on this order."));

        _lines.Remove(line);
        Recalculate();
        return Result.Success();
    }

    public Result ChangeLineQuantity(PurchaseOrderLineId lineId, OrderQuantity quantity)
    {
        var draft = EnsureDraft();
        if (draft.IsFailure) return draft;

        var line = FindLine(lineId);
        if (line is null)
            return Result.Failure(Error.NotFound("Purchasing.PurchaseOrder.LineNotFound", "The line was not found on this order."));

        line.Set(quantity, line.UnitCost);
        Recalculate();
        return Result.Success();
    }

    // ------------------------------------------------------------------ lifecycle

    public Result Submit()
    {
        var draft = EnsureDraft();
        if (draft.IsFailure) return draft;

        if (_lines.Count == 0)
            return Result.Failure(Error.Validation("Purchasing.PurchaseOrder.NoLines", "An order needs at least one line before it can be submitted."));

        Status = PurchaseOrderStatus.Submitted;
        SubmittedAt = DateTime.UtcNow;
        UpdatedAt = SubmittedAt.Value;
        return Result.Success();
    }

    /// <summary>
    /// Starts (or resumes) receiving into a warehouse. Allowed while goods are awaited (Submitted or PartiallyReceived). Once anything was
    /// received, later deliveries must use the same warehouse.
    /// </summary>
    public Result BeginReceiving(Guid warehouseId)
    {
        if (!IsAwaitingGoods)
            return Result.Failure(InvalidState("Only a placed order that still awaits goods can be received."));

        if (warehouseId == Guid.Empty)
            return Result.Failure(Error.Validation("Purchasing.PurchaseOrder.WarehouseRequired", "A warehouse is required to receive an order."));

        if (WarehouseId is { } current && current != warehouseId && _lines.Any(l => l.HasReceipts))
            return Result.Failure(Error.Conflict("Purchasing.PurchaseOrder.WarehouseMismatch",
                "Part of this order was already received into another warehouse; continue with that warehouse."));

        WarehouseId = warehouseId;
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }

    /// <summary>Records a delivery of one line (FIX-09: any part of what is still outstanding). Call <see cref="UpdateReceivingStatus"/> after the delivery.</summary>
    public Result ReceiveLine(PurchaseOrderLineId lineId, OrderQuantity quantity)
    {
        if (!IsAwaitingGoods)
            return Result.Failure(InvalidState("Lines can only be received on a placed order that still awaits goods."));

        var line = FindLine(lineId);
        if (line is null)
            return Result.Failure(Error.NotFound("Purchasing.PurchaseOrder.LineNotFound", "The line was not found on this order."));

        var r = line.Receive(quantity);
        if (r.IsFailure) return r;

        ReceivedAmount = _lines.Aggregate(Money.Zero, (acc, l) => acc + l.ReceivedTotal);
        UpdatedAt = DateTime.UtcNow;
        return r;
    }

    /// <summary>
    /// After a delivery: Received once every unit has arrived, PartiallyReceived while some has and the rest is still expected, unchanged
    /// (Submitted) while nothing has.
    /// </summary>
    public Result UpdateReceivingStatus()
    {
        if (!IsAwaitingGoods)
            return Result.Failure(InvalidState("Only a placed order that still awaits goods can be completed."));

        if (_lines.Count > 0 && _lines.All(l => l.IsReceived))
        {
            Status = PurchaseOrderStatus.Received;
            ReceivedAt = DateTime.UtcNow;
            UpdatedAt = ReceivedAt.Value;
        }
        else if (_lines.Any(l => l.HasReceipts))
        {
            Status = PurchaseOrderStatus.PartiallyReceived;
            UpdatedAt = DateTime.UtcNow;
        }

        return Result.Success();
    }

    /// <summary>Closes a partly received order short: what arrived stays in stock, the rest is no longer expected (FIX-09).</summary>
    public Result CloseShort(string reason)
    {
        if (Status != PurchaseOrderStatus.PartiallyReceived)
            return Result.Failure(InvalidState(Status == PurchaseOrderStatus.Submitted
                ? "Nothing of this order was received yet; cancel it instead of closing it."
                : "Only a partly received order can be closed short."));
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("Purchasing.PurchaseOrder.ClosingReasonRequired", "A reason is required to close an order short."));
        if (reason.Trim().Length > 500)
            return Result.Failure(Error.Validation("Purchasing.PurchaseOrder.ClosingReasonTooLong", "The reason cannot exceed 500 characters."));

        Status = PurchaseOrderStatus.Closed;
        ClosingReason = reason.Trim();
        ClosedAt = DateTime.UtcNow;
        UpdatedAt = ClosedAt.Value;
        return Result.Success();
    }

    public Result Cancel(string reason)
    {
        if (Status is PurchaseOrderStatus.Received)
            return Result.Failure(InvalidState("A received order cannot be cancelled."));
        if (Status is PurchaseOrderStatus.Closed)
            return Result.Failure(InvalidState("A closed order cannot be cancelled."));
        if (Status is PurchaseOrderStatus.Cancelled)
            return Result.Failure(InvalidState("The order is already cancelled."));
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("Purchasing.PurchaseOrder.CancellationReasonRequired", "A cancellation reason is required."));
        if (reason.Trim().Length > 500)
            return Result.Failure(Error.Validation("Purchasing.PurchaseOrder.CancellationReasonTooLong", "The reason cannot exceed 500 characters."));
        if (_lines.Any(l => l.HasReceipts))
            return Result.Failure(Error.Conflict("Purchasing.PurchaseOrder.PartiallyReceived",
                "Some goods were already received into stock; the order cannot be cancelled. Close it short instead."));

        Status = PurchaseOrderStatus.Cancelled;
        CancellationReason = reason.Trim();
        CancelledAt = DateTime.UtcNow;
        UpdatedAt = CancelledAt.Value;
        return Result.Success();
    }

    // ------------------------------------------------------------------ supplier returns (FIX-09b)

    /// <summary>
    /// Takes part of a received line back for a supplier return: never more than was received minus what was already returned. The line
    /// and the return are changed together; a refusal changes neither.
    /// </summary>
    public Result RecordReturn(SupplierReturn supplierReturn, PurchaseOrderLineId lineId, OrderQuantity quantity)
    {
        if (supplierReturn.PurchaseOrderId != Id)
            return Result.Failure(Error.Conflict("Purchasing.SupplierReturn.OtherOrder", "The return belongs to another order."));

        var line = FindLine(lineId);
        if (line is null)
            return Result.Failure(Error.NotFound("Purchasing.PurchaseOrder.LineNotFound", "The line was not found on this order."));

        var taken = line.Return(quantity);
        if (taken.IsFailure) return taken;

        supplierReturn.AddLine(line, quantity);
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }

    // ------------------------------------------------------------------ helpers

    private static Error InvalidState(string message) => Error.Conflict("Purchasing.PurchaseOrder.InvalidState", message);

    private Result EnsureDraft()
        => Status == PurchaseOrderStatus.Draft
            ? Result.Success()
            : Result.Failure(InvalidState($"Lines can only be changed on a Draft order. Current status: {Status}."));

    private Result<T> EnsureDraft<T>()
        => Status == PurchaseOrderStatus.Draft
            ? Result.Success<T>(default!)
            : Result.Failure<T>(InvalidState($"Lines can only be changed on a Draft order. Current status: {Status}."));

    private void Recalculate()
    {
        TotalAmount = _lines.Aggregate(Money.Zero, (acc, l) => acc + l.LineTotal);
        UpdatedAt = DateTime.UtcNow;
    }
}
