using Microsoft.EntityFrameworkCore;
using Inventory.Application.Repositories;
using Inventory.Domain.Entities;
using Inventory.Domain.ValueObjects;
using Inventory.Infrastructure.Persistence;

namespace Inventory.Infrastructure.Repositories;

internal sealed class EfLocationRepository(InventoryDbContext dbContext) : ILocationRepository
{
    public Task<Location?> GetByIdAsync(LocationId id, CancellationToken cancellationToken = default)
        => dbContext.Locations.FirstOrDefaultAsync(l => l.Id == id, cancellationToken);

    public Task<bool> ExistsByCodeInWarehouseAsync(
        WarehouseId warehouseId,
        string code,
        CancellationToken cancellationToken = default)
        => dbContext.Locations.AnyAsync(
            l => l.WarehouseId == warehouseId && l.Code == code.Trim().ToUpperInvariant(),
            cancellationToken);

    public async Task<IReadOnlyList<Location>> GetByWarehouseAsync(
        WarehouseId warehouseId,
        CancellationToken cancellationToken = default)
        => await dbContext.Locations
            .Where(l => l.WarehouseId == warehouseId)
            .OrderBy(l => l.Name)
            .ToListAsync(cancellationToken);

    public Task AddAsync(Location location, CancellationToken cancellationToken = default)
    {
        dbContext.Locations.Add(location);
        return Task.CompletedTask;
    }

    public void Update(Location location)
        => dbContext.Locations.Update(location);
}
