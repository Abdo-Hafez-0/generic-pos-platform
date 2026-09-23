namespace Inventory.Contracts.Models;

/// <summary>
/// Immutable DTO representing the current on-hand stock level for a StockItem.
///
/// This is the contract boundary — no Inventory domain entities cross this line.
/// Future modules (Sales, POS, Purchasing) consume StockLevelDto, never InventoryBalance.
///
/// Architecture reference: Module Map §18 (Inventory Contracts).
/// </summary>
public sealed record StockLevelDto(
    Guid StockItemId,
    Guid CatalogProductId,
    Guid WarehouseId,
    Guid? LocationId,
    decimal OnHand);
