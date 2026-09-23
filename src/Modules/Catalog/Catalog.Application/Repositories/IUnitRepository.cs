using Catalog.Domain.Entities;
using Catalog.Domain.ValueObjects;

namespace Catalog.Application.Repositories;

/// <summary>
/// Repository abstraction for Unit entities.
/// Defined in Application layer, implemented by Catalog.Infrastructure.
/// </summary>
public interface IUnitRepository
{
    Task<Unit?> GetByIdAsync(UnitId id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Unit>> GetAllActiveAsync(CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(UnitId id, CancellationToken cancellationToken = default);
    Task AddAsync(Unit unit, CancellationToken cancellationToken = default);
    void Update(Unit unit);
}
