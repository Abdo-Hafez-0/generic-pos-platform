using Microsoft.EntityFrameworkCore;
using Inventory.Application.Repositories;
using Inventory.Domain.Entities;
using Inventory.Domain.ValueObjects;
using Inventory.Infrastructure.Persistence;

namespace Inventory.Infrastructure.Repositories;

internal sealed class EfStockItemRepository(InventoryDbContext dbContext) : IStockItemRepository
{
    public Task<StockItem?> GetByIdAsync(StockItemId id, CancellationToken cancellationToken = default)
        => dbContext.StockItems.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    public Task<StockItem?> FindByProductAndWarehouseAsync(
        Guid catalogProductId,
        WarehouseId warehouseId,
        CancellationToken cancellationToken = default)
        => dbContext.StockItems.FirstOrDefaultAsync(
            s => s.CatalogProductId == catalogProductId && s.WarehouseId == warehouseId,
            cancellationToken);

    public async Task<IReadOnlyList<StockItem>> GetByWarehouseAsync(
        WarehouseId warehouseId,
        CancellationToken cancellationToken = default)
        => await dbContext.StockItems
            .Where(s => s.WarehouseId == warehouseId)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<StockItem>> GetAllAsync(CancellationToken cancellationToken = default)
        => await dbContext.StockItems.ToListAsync(cancellationToken);

    public Task AddAsync(StockItem stockItem, CancellationToken cancellationToken = default)
    {
        dbContext.StockItems.Add(stockItem);
        return Task.CompletedTask;
    }

    public void Update(StockItem stockItem)
        => dbContext.StockItems.Update(stockItem);
}
