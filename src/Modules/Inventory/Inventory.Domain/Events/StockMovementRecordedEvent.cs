using Inventory.Domain.Enums;
using Inventory.Domain.ValueObjects;

namespace Inventory.Domain.Events;

/// <summary>
/// Raised when a stock movement is recorded.
/// Represents the fact that inventory quantities changed.
/// </summary>
public sealed record StockMovementRecordedEvent(
    StockMovementId MovementId,
    StockItemId StockItemId,
    MovementType MovementType,
    decimal Quantity);
