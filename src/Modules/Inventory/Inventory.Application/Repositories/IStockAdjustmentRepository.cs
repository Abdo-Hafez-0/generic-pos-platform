using Inventory.Domain.Entities;
using Inventory.Domain.ValueObjects;

namespace Inventory.Application.Repositories;

/// <summary>
/// Repository abstraction for StockAdjustment records.
/// Defined in Application, implemented by Inventory.Infrastructure.
/// Adjustments are immutable after creation — no Update/Delete.
/// </summary>
public interface IStockAdjustmentRepository
{
    Task<StockAdjustment?> GetByIdAsync(StockAdjustmentId id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StockAdjustment>> GetByStockItemAsync(StockItemId stockItemId, CancellationToken cancellationToken = default);
    Task AddAsync(StockAdjustment adjustment, CancellationToken cancellationToken = default);
}
