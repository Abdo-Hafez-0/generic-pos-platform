using Inventory.Domain.Entities;
using Inventory.Domain.ValueObjects;

namespace Inventory.Application.Repositories;

/// <summary>
/// Repository abstraction for Warehouse aggregates.
/// Defined in Application, implemented by Inventory.Infrastructure.
/// </summary>
public interface IWarehouseRepository
{
    Task<Warehouse?> GetByIdAsync(WarehouseId id, CancellationToken cancellationToken = default);
    Task<Warehouse?> GetByCodeAsync(string code, CancellationToken cancellationToken = default);
    Task<bool> ExistsByCodeAsync(string code, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Warehouse>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Warehouse>> GetActiveAsync(CancellationToken cancellationToken = default);
    Task AddAsync(Warehouse warehouse, CancellationToken cancellationToken = default);
    void Update(Warehouse warehouse);
}
