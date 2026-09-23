using Microsoft.EntityFrameworkCore;
using Inventory.Application.Repositories;
using Inventory.Domain.Entities;
using Inventory.Domain.ValueObjects;
using Inventory.Infrastructure.Persistence;

namespace Inventory.Infrastructure.Repositories;

internal sealed class EfStockMovementRepository(InventoryDbContext dbContext) : IStockMovementRepository
{
    public Task<StockMovement?> GetByIdAsync(StockMovementId id, CancellationToken cancellationToken = default)
        => dbContext.StockMovements.FirstOrDefaultAsync(m => m.Id == id, cancellationToken);

    public async Task<IReadOnlyList<StockMovement>> GetByStockItemAsync(
        StockItemId stockItemId,
        CancellationToken cancellationToken = default)
        => await dbContext.StockMovements
            .Where(m => m.StockItemId == stockItemId)
            .OrderByDescending(m => m.OccurredAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<StockMovement>> GetRecentAsync(
        int limit,
        CancellationToken cancellationToken = default)
        => await dbContext.StockMovements
            .OrderByDescending(m => m.OccurredAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public Task AddAsync(StockMovement movement, CancellationToken cancellationToken = default)
    {
        dbContext.StockMovements.Add(movement);
        return Task.CompletedTask;
    }
}
