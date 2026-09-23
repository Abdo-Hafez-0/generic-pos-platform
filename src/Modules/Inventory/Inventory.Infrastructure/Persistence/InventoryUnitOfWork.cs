using Inventory.Application.Abstractions;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// Inventory module Unit of Work — wraps InventoryDbContext.SaveChangesAsync.
/// </summary>
internal sealed class InventoryUnitOfWork(InventoryDbContext dbContext) : IInventoryUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => dbContext.SaveChangesAsync(cancellationToken);
}
