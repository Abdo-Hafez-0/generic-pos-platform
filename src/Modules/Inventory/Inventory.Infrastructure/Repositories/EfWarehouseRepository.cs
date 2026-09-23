using Microsoft.EntityFrameworkCore;
using Inventory.Application.Repositories;
using Inventory.Domain.Entities;
using Inventory.Domain.ValueObjects;
using Inventory.Infrastructure.Persistence;

namespace Inventory.Infrastructure.Repositories;

internal sealed class EfWarehouseRepository(InventoryDbContext dbContext) : IWarehouseRepository
{
    public Task<Warehouse?> GetByIdAsync(WarehouseId id, CancellationToken cancellationToken = default)
        => dbContext.Warehouses.FirstOrDefaultAsync(w => w.Id == id, cancellationToken);

    public Task<Warehouse?> GetByCodeAsync(string code, CancellationToken cancellationToken = default)
        => dbContext.Warehouses.FirstOrDefaultAsync(
            w => w.Code == code.Trim().ToUpperInvariant(), cancellationToken);

    public Task<bool> ExistsByCodeAsync(string code, CancellationToken cancellationToken = default)
        => dbContext.Warehouses.AnyAsync(
            w => w.Code == code.Trim().ToUpperInvariant(), cancellationToken);

    public async Task<IReadOnlyList<Warehouse>> GetAllAsync(CancellationToken cancellationToken = default)
        => await dbContext.Warehouses.OrderBy(w => w.Name).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Warehouse>> GetActiveAsync(CancellationToken cancellationToken = default)
        => await dbContext.Warehouses.Where(w => w.IsActive).OrderBy(w => w.Name).ToListAsync(cancellationToken);

    public Task AddAsync(Warehouse warehouse, CancellationToken cancellationToken = default)
    {
        dbContext.Warehouses.Add(warehouse);
        return Task.CompletedTask;
    }

    public void Update(Warehouse warehouse)
        => dbContext.Warehouses.Update(warehouse);
}
