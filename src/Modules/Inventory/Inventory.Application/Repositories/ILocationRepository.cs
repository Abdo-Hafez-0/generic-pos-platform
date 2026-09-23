using Inventory.Domain.Entities;
using Inventory.Domain.ValueObjects;

namespace Inventory.Application.Repositories;

/// <summary>
/// Repository abstraction for Location aggregates.
/// Defined in Application, implemented by Inventory.Infrastructure.
/// </summary>
public interface ILocationRepository
{
    Task<Location?> GetByIdAsync(LocationId id, CancellationToken cancellationToken = default);
    Task<bool> ExistsByCodeInWarehouseAsync(WarehouseId warehouseId, string code, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Location>> GetByWarehouseAsync(WarehouseId warehouseId, CancellationToken cancellationToken = default);
    Task AddAsync(Location location, CancellationToken cancellationToken = default);
    void Update(Location location);
}
