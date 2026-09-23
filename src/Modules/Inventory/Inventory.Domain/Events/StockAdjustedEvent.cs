using Inventory.Domain.Enums;
using Inventory.Domain.ValueObjects;

namespace Inventory.Domain.Events;

/// <summary>
/// Raised when a stock adjustment is applied.
/// Conveys the signed quantity change and the reason for the adjustment.
/// </summary>
public sealed record StockAdjustedEvent(
    StockAdjustmentId AdjustmentId,
    StockItemId StockItemId,
    decimal AdjustmentQuantity,
    AdjustmentReason Reason);
