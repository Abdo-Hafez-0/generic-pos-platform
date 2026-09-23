using Platform.Core.Results;
using Inventory.Domain.Enums;
using Inventory.Domain.Events;
using Inventory.Domain.ValueObjects;

namespace Inventory.Domain.Entities;

/// <summary>
/// A stock adjustment — an intentional, auditable correction of inventory quantities.
///
/// StockAdjustment represents the INTENT behind a correction (who, why, how much).
/// It drives the creation of a StockMovement (the FACT of the correction).
///
/// DESIGN:
///   AdjustmentQuantity is signed:
///     positive → stock being added (e.g., found stock)
///     negative → stock being removed (e.g., damage write-off)
///
///   The Application layer converts this to an appropriate StockMovement(Adjustment)
///   with a positive Quantity and the correct direction.
///
/// INVARIANTS:
/// - AdjustmentQuantity cannot be zero (no-op adjustment is meaningless).
/// - StockItemId cannot be empty.
/// - Notes are required when Reason == Other.
/// - Notes max 500 chars.
///
/// Architecture: Inventory.Domain — no EF Core, no WPF, no HTTP.
/// </summary>
public sealed class StockAdjustment
{
    private readonly List<object> _domainEvents = [];

    private StockAdjustment() { }

    public StockAdjustmentId Id { get; private set; }
    public StockItemId StockItemId { get; private set; }

    /// <summary>
    /// The signed quantity of the adjustment.
    /// Positive = stock added, Negative = stock removed.
    /// Cannot be zero.
    /// </summary>
    public decimal AdjustmentQuantity { get; private set; }

    public AdjustmentReason Reason { get; private set; }

    /// <summary>
    /// Required when Reason == Other.
    /// Optional for other reasons but always stored when provided.
    /// </summary>
    public string? Notes { get; private set; }

    public DateTime AdjustedAt { get; private set; }

    /// <summary>Domain events raised during this aggregate's lifetime.</summary>
    public IReadOnlyList<object> DomainEvents => _domainEvents.AsReadOnly();

    /// <summary>Clears collected domain events after dispatch.</summary>
    public void ClearDomainEvents() => _domainEvents.Clear();

    // -----------------------------------------------------------------------
    // Factory
    // -----------------------------------------------------------------------

    /// <summary>
    /// Records a stock adjustment.
    /// The adjustmentQuantity may be positive (add) or negative (remove) but not zero.
    /// </summary>
    public static Result<StockAdjustment> Create(
        StockItemId stockItemId,
        decimal adjustmentQuantity,
        AdjustmentReason reason,
        string? notes = null)
    {
        if (stockItemId == StockItemId.Empty)
            return Result.Failure<StockAdjustment>(Error.Validation(
                "Inventory.StockAdjustment.StockItemRequired",
                "A valid stock item must be referenced."));

        if (adjustmentQuantity == 0m)
            return Result.Failure<StockAdjustment>(Error.Validation(
                "Inventory.StockAdjustment.ZeroQuantity",
                "Adjustment quantity cannot be zero. Use a positive value to add stock or negative to remove."));

        if (reason == AdjustmentReason.Other && string.IsNullOrWhiteSpace(notes))
            return Result.Failure<StockAdjustment>(Error.Validation(
                "Inventory.StockAdjustment.NotesRequiredForOther",
                "Notes are required when adjustment reason is 'Other'."));

        if (notes is not null && notes.Length > 500)
            return Result.Failure<StockAdjustment>(Error.Validation(
                "Inventory.StockAdjustment.NotesTooLong",
                "Adjustment notes cannot exceed 500 characters."));

        var adjustment = new StockAdjustment
        {
            Id = StockAdjustmentId.New(),
            StockItemId = stockItemId,
            AdjustmentQuantity = adjustmentQuantity,
            Reason = reason,
            Notes = notes?.Trim(),
            AdjustedAt = DateTime.UtcNow
        };

        adjustment._domainEvents.Add(
            new StockAdjustedEvent(adjustment.Id, stockItemId, adjustmentQuantity, reason));

        return Result.Success(adjustment);
    }
}
