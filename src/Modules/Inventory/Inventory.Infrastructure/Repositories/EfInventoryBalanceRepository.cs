using Microsoft.EntityFrameworkCore;
using Inventory.Application.Repositories;
using Inventory.Domain.Entities;
using Inventory.Domain.ValueObjects;
using Inventory.Infrastructure.Persistence;

namespace Inventory.Infrastructure.Repositories;

internal sealed class EfInventoryBalanceRepository(InventoryDbContext dbContext) : IInventoryBalanceRepository
{
    public Task<InventoryBalance?> GetByStockItemAsync(
        StockItemId stockItemId,
        CancellationToken cancellationToken = default)
        => dbContext.InventoryBalances.FirstOrDefaultAsync(b => b.StockItemId == stockItemId, cancellationToken);

    public async Task<IReadOnlyList<InventoryBalance>> GetAllAsync(CancellationToken cancellationToken = default)
        => await dbContext.InventoryBalances.ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<InventoryBalance>> GetByWarehouseAsync(
        WarehouseId warehouseId,
        CancellationToken cancellationToken = default)
    {
        // Join through StockItems to filter by warehouse
        var stockItemIds = await dbContext.StockItems
            .Where(s => s.WarehouseId == warehouseId)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);

        return await dbContext.InventoryBalances
            .Where(b => stockItemIds.Contains(b.StockItemId))
            .ToListAsync(cancellationToken);
    }

    public Task AddAsync(InventoryBalance balance, CancellationToken cancellationToken = default)
    {
        dbContext.InventoryBalances.Add(balance);
        return Task.CompletedTask;
    }

    public void Update(InventoryBalance balance)
        => dbContext.InventoryBalances.Update(balance);
}
