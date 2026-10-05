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

    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public DateTime? SubmittedAt { get; private set; }
    public DateTime? ReceivedAt { get; private set; }
    public DateTime? CancelledAt { get; private set; }
    public string? CancellationReason { get; private set; }

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

    /// <summary>Starts (or resumes) receiving into a warehouse. Allowed while Submitted. A resumed receipt must use the same warehouse.</summary>
    public Result BeginReceiving(Guid warehouseId)
    {
        if (Status != PurchaseOrderStatus.Submitted)
            return Result.Failure(InvalidState("Only a Submitted order can be received."));

        if (warehouseId == Guid.Empty)
            return Result.Failure(Error.Validation("Purchasing.PurchaseOrder.WarehouseRequired", "A warehouse is required to receive an order."));

        if (WarehouseId is { } current && current != warehouseId && _lines.Any(l => l.IsReceived))
            return Result.Failure(Error.Conflict("Purchasing.PurchaseOrder.WarehouseMismatch",
                "Part of this order was already received into another warehouse; continue with that warehouse."));

        WarehouseId = warehouseId;
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }

    public Result MarkLineReceived(PurchaseOrderLineId lineId)
    {
        if (Status != PurchaseOrderStatus.Submitted)
            return Result.Failure(InvalidState("Lines can only be received on a Submitted order."));

        var line = FindLine(lineId);
        if (line is null)
            return Result.Failure(Error.NotFound("Purchasing.PurchaseOrder.LineNotFound", "The line was not found on this order."));

        var r = line.MarkReceived();
        if (r.IsSuccess) UpdatedAt = DateTime.UtcNow;
        return r;
    }

    /// <summary>Moves to Received once every line has been received. A no-op (success) while lines are still outstanding.</summary>
    public Result CompleteIfFullyReceived()
    {
        if (Status != PurchaseOrderStatus.Submitted)
            return Result.Failure(InvalidState("Only a Submitted order can be completed."));

        if (_lines.Count == 0 || _lines.Any(l => !l.IsReceived))
            return Result.Success();

        Status = PurchaseOrderStatus.Received;
        ReceivedAt = DateTime.UtcNow;
        UpdatedAt = ReceivedAt.Value;
        return Result.Success();
    }

    public Result Cancel(string reason)
    {
        if (Status is PurchaseOrderStatus.Received)
            return Result.Failure(InvalidState("A received order cannot be cancelled."));
        if (Status is PurchaseOrderStatus.Cancelled)
            return Result.Failure(InvalidState("The order is already cancelled."));
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("Purchasing.PurchaseOrder.CancellationReasonRequired", "A cancellation reason is required."));
        if (reason.Trim().Length > 500)
            return Result.Failure(Error.Validation("Purchasing.PurchaseOrder.CancellationReasonTooLong", "The reason cannot exceed 500 characters."));
        if (_lines.Any(l => l.IsReceived))
            return Result.Failure(Error.Conflict("Purchasing.PurchaseOrder.PartiallyReceived",
                "Some lines were already received into stock; the order cannot be cancelled."));

        Status = PurchaseOrderStatus.Cancelled;
        CancellationReason = reason.Trim();
        CancelledAt = DateTime.UtcNow;
        UpdatedAt = CancelledAt.Value;
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
