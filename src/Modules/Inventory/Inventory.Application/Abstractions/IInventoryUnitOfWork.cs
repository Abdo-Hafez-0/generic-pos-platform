namespace Inventory.Application.Abstractions;

/// <summary>
/// Unit of Work abstraction for the Inventory module.
/// Saves changes to the InventoryDbContext.
/// Implemented by Inventory.Infrastructure.Persistence.InventoryUnitOfWork.
/// </summary>
public interface IInventoryUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
