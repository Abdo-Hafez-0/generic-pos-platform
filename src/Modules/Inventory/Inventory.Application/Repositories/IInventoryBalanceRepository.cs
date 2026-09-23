using Inventory.Domain.Entities;
using Inventory.Domain.ValueObjects;

namespace Inventory.Application.Repositories;

/// <summary>
/// Repository abstraction for InventoryBalance read-models.
/// Defined in Application, implemented by Inventory.Infrastructure.
/// </summary>
public interface IInventoryBalanceRepository
{
    Task<InventoryBalance?> GetByStockItemAsync(StockItemId stockItemId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InventoryBalance>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InventoryBalance>> GetByWarehouseAsync(WarehouseId warehouseId, CancellationToken cancellationToken = default);
    Task AddAsync(InventoryBalance balance, CancellationToken cancellationToken = default);
    void Update(InventoryBalance balance);
}
