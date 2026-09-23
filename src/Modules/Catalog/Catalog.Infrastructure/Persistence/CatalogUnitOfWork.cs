using Catalog.Application.Abstractions;
using Catalog.Infrastructure.Persistence;

namespace Catalog.Infrastructure.Persistence;

/// <summary>
/// Catalog module Unit of Work — wraps CatalogDbContext.SaveChangesAsync().
/// Registered in DI as ICatalogUnitOfWork (Scoped, same lifetime as CatalogDbContext).
/// </summary>
internal sealed class CatalogUnitOfWork(CatalogDbContext dbContext) : ICatalogUnitOfWork
{
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        await dbContext.SaveChangesAsync(cancellationToken);
}
