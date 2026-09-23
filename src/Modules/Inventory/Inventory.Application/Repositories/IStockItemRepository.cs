using Inventory.Domain.Entities;
using Inventory.Domain.ValueObjects;

namespace Inventory.Application.Repositories;

/// <summary>
/// Repository abstraction for StockItem aggregates.
/// Defined in Application, implemented by Inventory.Infrastructure.
/// </summary>
public interface IStockItemRepository
{
    Task<StockItem?> GetByIdAsync(StockItemId id, CancellationToken cancellationToken = default);
    Task<StockItem?> FindByProductAndWarehouseAsync(
        Guid catalogProductId,
        WarehouseId warehouseId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StockItem>> GetByWarehouseAsync(WarehouseId warehouseId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StockItem>> GetAllAsync(CancellationToken cancellationToken = default);
    Task AddAsync(StockItem stockItem, CancellationToken cancellationToken = default);
    void Update(StockItem stockItem);
}
