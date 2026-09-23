namespace Inventory.Contracts.Models;

/// <summary>
/// Immutable DTO representing a Warehouse as seen by other modules.
///
/// Consumed by Sales, POS, etc. through Inventory.Contracts.
/// Never exposes Inventory domain entities.
/// </summary>
public sealed record WarehouseDto(
    Guid WarehouseId,
    string Name,
    string Code,
    bool IsActive);
