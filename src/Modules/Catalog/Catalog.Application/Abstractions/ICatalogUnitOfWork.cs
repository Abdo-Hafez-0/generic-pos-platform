namespace Catalog.Application.Abstractions;

/// <summary>
/// Unit of Work abstraction for the Catalog module.
/// Saves changes to the CatalogDbContext.
/// </summary>
public interface ICatalogUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
