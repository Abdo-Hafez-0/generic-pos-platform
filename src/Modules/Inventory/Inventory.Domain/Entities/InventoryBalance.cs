using Platform.Core.Results;
using Inventory.Domain.ValueObjects;

namespace Inventory.Domain.Entities;

/// <summary>
/// The InventoryBalance — the current on-hand quantity for a StockItem.
///
/// DESIGN: This is a maintained read-model (not a sourced view).
/// It is updated atomically with each StockMovement in the same transaction.
/// This gives O(1) current-stock reads without re-aggregating all movements.
///
/// The balance has the same ID as the StockItem it represents (1-to-1 relationship).
///
/// INVARIANTS:
/// - OnHand cannot be negative (enforced by this aggregate on every update).
/// - StockItemId cannot be empty.
///
/// Architecture: Inventory.Domain — no EF Core, no WPF, no HTTP.
/// </summary>
public sealed class InventoryBalance
{
    private InventoryBalance() { }

    /// <summary>Same as the owning StockItem's ID — 1-to-1 relationship.</summary>
    public StockItemId StockItemId { get; private set; }

    /// <summary>Current on-hand quantity. Cannot be negative.</summary>
    public Quantity OnHand { get; private set; }

    public DateTime UpdatedAt { get; private set; }

    // -----------------------------------------------------------------------
    // Factory
    // -----------------------------------------------------------------------

    /// <summary>
    /// Creates a new InventoryBalance for a newly registered StockItem.
    /// Starts at zero quantity.
    /// </summary>
    public static Result<InventoryBalance> Create(StockItemId stockItemId)
    {
        if (stockItemId == StockItemId.Empty)
            return Result.Failure<InventoryBalance>(Error.Validation(
                "Inventory.Balance.StockItemRequired",
                "A valid stock item ID must be provided."));

        return Result.Success(new InventoryBalance
        {
            StockItemId = stockItemId,
            OnHand = Quantity.Zero,
            UpdatedAt = DateTime.UtcNow
        });
    }

    // -----------------------------------------------------------------------
    // Behaviour
    // -----------------------------------------------------------------------

    /// <summary>Increases the on-hand quantity (e.g., stock received).</summary>
    public Result Increase(Quantity amount)
    {
        if (amount <= Quantity.Zero)
            return Result.Failure(Error.Validation(
                "Inventory.Balance.IncreaseAmountMustBePositive",
                "Amount to increase must be greater than zero."));

        OnHand = OnHand.Add(amount);
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }

    /// <summary>
    /// Decreases the on-hand quantity (e.g., stock sold or consumed).
    /// Returns a failure if the result would go below zero (insufficient stock).
    /// </summary>
    public Result Decrease(Quantity amount)
    {
        if (amount <= Quantity.Zero)
            return Result.Failure(Error.Validation(
                "Inventory.Balance.DecreaseAmountMustBePositive",
                "Amount to decrease must be greater than zero."));

        var subtractResult = OnHand.Subtract(amount);
        if (subtractResult.IsFailure)
            return Result.Failure(subtractResult.Error);

        OnHand = subtractResult.Value;
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }

    /// <summary>
    /// Applies a signed adjustment (positive = increase, negative = decrease).
    /// Negative adjustments that drive balance below zero are rejected.
    /// </summary>
    public Result ApplyAdjustment(decimal signedAmount)
    {
        if (signedAmount == 0m)
            return Result.Failure(Error.Validation(
                "Inventory.Balance.ZeroAdjustment",
                "Adjustment amount cannot be zero."));

        if (signedAmount > 0)
        {
            var addQty = Quantity.Create(signedAmount);
            if (addQty.IsFailure) return Result.Failure(addQty.Error);
            return Increase(addQty.Value);
        }
        else
        {
            var removeQty = Quantity.Create(Math.Abs(signedAmount));
            if (removeQty.IsFailure) return Result.Failure(removeQty.Error);
            return Decrease(removeQty.Value);
        }
    }
}
