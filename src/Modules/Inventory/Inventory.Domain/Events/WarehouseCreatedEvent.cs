using Inventory.Domain.ValueObjects;

namespace Inventory.Domain.Events;

/// <summary>
/// Raised when a new Warehouse is created.
/// </summary>
public sealed record WarehouseCreatedEvent(
    WarehouseId WarehouseId,
    string Name,
    string Code);
