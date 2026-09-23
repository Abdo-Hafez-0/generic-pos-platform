using Microsoft.EntityFrameworkCore;
using Inventory.Contracts.Interfaces;
using Inventory.Contracts.Models;
using Inventory.Infrastructure.Persistence;

namespace Inventory.Infrastructure.Services;

/// <summary>
/// Implements IInventoryReader from Inventory.Contracts.
///
/// This is the cross-module contract implementation — consumed by Sales, POS, Reporting.
/// It returns DTOs only — never Inventory domain entities.
/// </summary>
internal sealed class InventoryReader(InventoryDbContext dbContext) : IInventoryReader
{
    public async Task<StockLevelDto?> GetStockLevelAsync(
        Guid stockItemId,
        CancellationToken cancellationToken = default)
    {
        var sid = new Inventory.Domain.ValueObjects.StockItemId(stockItemId);
        var stockItem = await dbContext.StockItems
            .FirstOrDefaultAsync(s => s.Id == sid, cancellationToken);

        if (stockItem is null) return null;

        var balance = await dbContext.InventoryBalances
            .FirstOrDefaultAsync(b => b.StockItemId == stockItem.Id, cancellationToken);

        return new StockLevelDto(
            stockItem.Id.Value,
            stockItem.CatalogProductId,
            stockItem.WarehouseId.Value,
            stockItem.LocationId?.Value,
            balance?.OnHand.Value ?? 0m);
    }

    public async Task<StockLevelDto?> GetStockLevelByProductAsync(
        Guid catalogProductId,
        Guid warehouseId,
        CancellationToken cancellationToken = default)
    {
        var wid = new Inventory.Domain.ValueObjects.WarehouseId(warehouseId);
        var stockItem = await dbContext.StockItems
            .FirstOrDefaultAsync(
                s => s.CatalogProductId == catalogProductId && s.WarehouseId == wid,
                cancellationToken);

        if (stockItem is null) return null;

        var balance = await dbContext.InventoryBalances
            .FirstOrDefaultAsync(b => b.StockItemId == stockItem.Id, cancellationToken);

        return new StockLevelDto(
            stockItem.Id.Value,
            stockItem.CatalogProductId,
            stockItem.WarehouseId.Value,
            stockItem.LocationId?.Value,
            balance?.OnHand.Value ?? 0m);
    }

    public async Task<IReadOnlyList<StockLevelDto>> GetAllStockLevelsAsync(
        CancellationToken cancellationToken = default)
    {
        var stockItems = await dbContext.StockItems.ToListAsync(cancellationToken);
        var balances = await dbContext.InventoryBalances.ToListAsync(cancellationToken);

        var balanceLookup = balances.ToDictionary(b => b.StockItemId);

        return stockItems.Select(item =>
        {
            var onHand = balanceLookup.TryGetValue(item.Id, out var bal) ? bal.OnHand.Value : 0m;
            return new StockLevelDto(
                item.Id.Value,
                item.CatalogProductId,
                item.WarehouseId.Value,
                item.LocationId?.Value,
                onHand);
        }).ToList();
    }

    public async Task<IReadOnlyList<WarehouseDto>> GetAllWarehousesAsync(
        CancellationToken cancellationToken = default)
    {
        var warehouses = await dbContext.Warehouses
            .OrderBy(w => w.Name)
            .ToListAsync(cancellationToken);

        return warehouses.Select(w => new WarehouseDto(w.Id.Value, w.Name, w.Code, w.IsActive)).ToList();
    }
}
