namespace Suppliers.Application.Abstractions;

/// <summary>Saves changes to the Suppliers module's own persistence (SuppliersDbContext).</summary>
public interface ISuppliersUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
