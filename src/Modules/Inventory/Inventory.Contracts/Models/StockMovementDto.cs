namespace Inventory.Contracts.Models;

/// <summary>
/// Immutable DTO representing a historical stock movement.
///
/// Consumed by future modules (Purchasing, Reporting) through Inventory.Contracts.
/// Never exposes Inventory domain entities.
/// </summary>
public sealed record StockMovementDto(
    Guid MovementId,
    Guid StockItemId,
    string MovementType,
    decimal Quantity,
    string? Reference,
    DateTime OccurredAt);
