using Microsoft.EntityFrameworkCore;
using Inventory.Contracts.Interfaces;
using Inventory.Contracts.Models;
using Inventory.Infrastructure.Persistence;

namespace Inventory.Infrastructure.Services;

/// <summary>
/// Implements IStockMovementReader from Inventory.Contracts.
/// Returns historical movement records as DTOs — no domain types exposed.
/// </summary>
internal sealed class StockMovementReader(InventoryDbContext dbContext) : IStockMovementReader
{
    public async Task<IReadOnlyList<StockMovementDto>> GetMovementsForStockItemAsync(
        Guid stockItemId,
        CancellationToken cancellationToken = default)
    {
        var sid = new Inventory.Domain.ValueObjects.StockItemId(stockItemId);
        var movements = await dbContext.StockMovements
            .Where(m => m.StockItemId == sid)
            .OrderByDescending(m => m.OccurredAt)
            .ToListAsync(cancellationToken);

        return movements.Select(m => new StockMovementDto(
            m.Id.Value,
            m.StockItemId.Value,
            m.MovementType.ToString(),
            m.Quantity.Value,
            m.Reference,
            m.OccurredAt)).ToList();
    }

    public async Task<IReadOnlyList<StockMovementDto>> GetRecentMovementsAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var movements = await dbContext.StockMovements
            .OrderByDescending(m => m.OccurredAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return movements.Select(m => new StockMovementDto(
            m.Id.Value,
            m.StockItemId.Value,
            m.MovementType.ToString(),
            m.Quantity.Value,
            m.Reference,
            m.OccurredAt)).ToList();
    }
}
