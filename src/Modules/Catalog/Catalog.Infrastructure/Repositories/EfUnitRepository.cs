using Microsoft.EntityFrameworkCore;
using Catalog.Application.Repositories;
using Catalog.Domain.Entities;
using Catalog.Domain.ValueObjects;
using Catalog.Infrastructure.Persistence;

namespace Catalog.Infrastructure.Repositories;

internal sealed class EfUnitRepository(CatalogDbContext dbContext) : IUnitRepository
{
    public async Task<Unit?> GetByIdAsync(UnitId id, CancellationToken cancellationToken = default) =>
        await dbContext.Units.FindAsync([id], cancellationToken);

    public async Task<IReadOnlyList<Unit>> GetAllActiveAsync(CancellationToken cancellationToken = default) =>
        await dbContext.Units
            .Where(u => u.IsActive)
            .OrderBy(u => u.Name)
            .ToListAsync(cancellationToken);

    public async Task<bool> ExistsAsync(UnitId id, CancellationToken cancellationToken = default) =>
        await dbContext.Units.AnyAsync(u => u.Id == id, cancellationToken);

    public async Task AddAsync(Unit unit, CancellationToken cancellationToken = default) =>
        await dbContext.Units.AddAsync(unit, cancellationToken);

    public void Update(Unit unit) =>
        dbContext.Units.Update(unit);
}
