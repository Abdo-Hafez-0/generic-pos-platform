using Platform.Core.Results;
using Inventory.Domain.Enums;
using Inventory.Domain.Events;
using Inventory.Domain.ValueObjects;

namespace Inventory.Domain.Entities;

/// <summary>
/// An append-only stock movement record.
///
/// Represents a single stock-changing fact: a quantity of an item moved in, out,
/// was adjusted, or was transferred. StockMovements are immutable after creation —
/// they form the audit trail of all stock changes.
///
/// DESIGN: StockMovements are created by the Application layer after business rules
/// are validated (e.g., AddStockCommandHandler, AdjustStockCommandHandler).
/// The quantity is always positive — the MovementType determines direction.
///
/// INVARIANTS:
/// - Quantity must be > 0.
/// - StockItemId cannot be empty.
/// - Reference is optional but max 200 chars when provided.
///
/// Architecture: Inventory.Domain — no EF Core, no WPF, no HTTP.
/// </summary>
public sealed class StockMovement
{
    private readonly List<object> _domainEvents = [];

    private StockMovement() { }

    public StockMovementId Id { get; private set; }
    public StockItemId StockItemId { get; private set; }
    public MovementType MovementType { get; private set; }

    /// <summary>The magnitude of the stock change. Always positive — direction determined by MovementType.</summary>
    public Quantity Quantity { get; private set; }

    /// <summary>Optional external reference (e.g., purchase order number, sale ID, adjustment note).</summary>
    public string? Reference { get; private set; }

    public DateTime OccurredAt { get; private set; }

    /// <summary>Domain events raised during this aggregate's lifetime.</summary>
    public IReadOnlyList<object> DomainEvents => _domainEvents.AsReadOnly();

    /// <summary>Clears collected domain events after dispatch.</summary>
    public void ClearDomainEvents() => _domainEvents.Clear();

    // -----------------------------------------------------------------------
    // Factory
    // -----------------------------------------------------------------------

    /// <summary>Records a stock movement fact. Quantity must be strictly positive.</summary>
    public static Result<StockMovement> Record(
        StockItemId stockItemId,
        MovementType movementType,
        Quantity quantity,
        string? reference = null)
    {
        if (stockItemId == StockItemId.Empty)
            return Result.Failure<StockMovement>(Error.Validation(
                "Inventory.StockMovement.StockItemRequired",
                "A valid stock item must be referenced."));

        if (quantity <= Quantity.Zero)
            return Result.Failure<StockMovement>(Error.Validation(
                "Inventory.StockMovement.QuantityMustBePositive",
                "Stock movement quantity must be greater than zero."));

        if (reference is not null && reference.Length > 200)
            return Result.Failure<StockMovement>(Error.Validation(
                "Inventory.StockMovement.ReferenceTooLong",
                "Movement reference cannot exceed 200 characters."));

        var movement = new StockMovement
        {
            Id = StockMovementId.New(),
            StockItemId = stockItemId,
            MovementType = movementType,
            Quantity = quantity,
            Reference = reference?.Trim(),
            OccurredAt = DateTime.UtcNow
        };

        movement._domainEvents.Add(
            new StockMovementRecordedEvent(movement.Id, stockItemId, movementType, quantity.Value));

        return Result.Success(movement);
    }
}
