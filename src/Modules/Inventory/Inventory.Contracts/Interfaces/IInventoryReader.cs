using Inventory.Contracts.Models;

namespace Inventory.Contracts.Interfaces;

/// <summary>
/// Allows other modules to read current inventory state.
///
/// Implemented by Inventory.Infrastructure.Services.InventoryReader.
/// Consumed by future modules: Sales, POS, Purchasing, Reporting.
///
/// Cross-module contract: never exposes Inventory.Domain types.
/// Architecture reference: Architecture §18 (Module.Contracts).
/// </summary>
public interface IInventoryReader
{
    /// <summary>Gets the current stock level for a specific stock item. Returns null if not found.</summary>
    Task<StockLevelDto?> GetStockLevelAsync(Guid stockItemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current stock level for a catalog product in a specific warehouse.
    /// Returns null if no StockItem is registered for this product/warehouse combination.
    /// </summary>
    Task<StockLevelDto?> GetStockLevelByProductAsync(
        Guid catalogProductId,
        Guid warehouseId,
        CancellationToken cancellationToken = default);

    /// <summary>Gets all current stock levels. Suitable for stock overview screens.</summary>
    Task<IReadOnlyList<StockLevelDto>> GetAllStockLevelsAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets all warehouses.</summary>
    Task<IReadOnlyList<WarehouseDto>> GetAllWarehousesAsync(CancellationToken cancellationToken = default);
}
