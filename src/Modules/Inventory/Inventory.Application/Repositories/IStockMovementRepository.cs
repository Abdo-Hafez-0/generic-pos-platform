using Inventory.Domain.Entities;
using Inventory.Domain.ValueObjects;

namespace Inventory.Application.Repositories;

/// <summary>
/// Repository abstraction for StockMovement records.
/// Defined in Application, implemented by Inventory.Infrastructure.
/// StockMovements are append-only — no Update/Delete methods.
/// </summary>
public interface IStockMovementRepository
{
    Task<StockMovement?> GetByIdAsync(StockMovementId id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StockMovement>> GetByStockItemAsync(StockItemId stockItemId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StockMovement>> GetRecentAsync(int limit, CancellationToken cancellationToken = default);
    Task AddAsync(StockMovement movement, CancellationToken cancellationToken = default);
}
