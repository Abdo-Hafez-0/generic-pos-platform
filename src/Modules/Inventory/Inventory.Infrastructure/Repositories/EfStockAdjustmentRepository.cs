using Microsoft.EntityFrameworkCore;
using Inventory.Application.Repositories;
using Inventory.Domain.Entities;
using Inventory.Domain.ValueObjects;
using Inventory.Infrastructure.Persistence;

namespace Inventory.Infrastructure.Repositories;

internal sealed class EfStockAdjustmentRepository(InventoryDbContext dbContext) : IStockAdjustmentRepository
{
    public Task<StockAdjustment?> GetByIdAsync(StockAdjustmentId id, CancellationToken cancellationToken = default)
        => dbContext.StockAdjustments.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

    public async Task<IReadOnlyList<StockAdjustment>> GetByStockItemAsync(
        StockItemId stockItemId,
        CancellationToken cancellationToken = default)
        => await dbContext.StockAdjustments
            .Where(a => a.StockItemId == stockItemId)
            .OrderByDescending(a => a.AdjustedAt)
            .ToListAsync(cancellationToken);

    public Task AddAsync(StockAdjustment adjustment, CancellationToken cancellationToken = default)
    {
        dbContext.StockAdjustments.Add(adjustment);
        return Task.CompletedTask;
    }
}
