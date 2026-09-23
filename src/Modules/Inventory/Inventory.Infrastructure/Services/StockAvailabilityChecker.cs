using Microsoft.EntityFrameworkCore;
using Inventory.Contracts.Interfaces;
using Inventory.Infrastructure.Persistence;

namespace Inventory.Infrastructure.Services;

/// <summary>
/// Implements IStockAvailabilityChecker from Inventory.Contracts.
/// Used by Sales/POS to check if enough stock exists before committing a sale.
/// Read-only — no state mutations.
/// </summary>
internal sealed class StockAvailabilityChecker(InventoryDbContext dbContext) : IStockAvailabilityChecker
{
    public async Task<bool> IsAvailableAsync(
        Guid catalogProductId,
        Guid warehouseId,
        decimal requiredQuantity,
        CancellationToken cancellationToken = default)
    {
        if (requiredQuantity <= 0) return false;

        var wid = new Inventory.Domain.ValueObjects.WarehouseId(warehouseId);

        var stockItem = await dbContext.StockItems
            .FirstOrDefaultAsync(
                s => s.CatalogProductId == catalogProductId && s.WarehouseId == wid && s.IsActive,
                cancellationToken);

        if (stockItem is null) return false;

        var balance = await dbContext.InventoryBalances
            .FirstOrDefaultAsync(b => b.StockItemId == stockItem.Id, cancellationToken);

        if (balance is null) return false;

        return balance.OnHand.Value >= requiredQuantity;
    }
}
