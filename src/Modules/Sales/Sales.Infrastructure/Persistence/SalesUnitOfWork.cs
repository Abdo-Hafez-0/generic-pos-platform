using Sales.Application.Abstractions;

namespace Sales.Infrastructure.Persistence;

/// <summary>
/// ISalesUnitOfWork implementation for the Sales module.
/// Delegates SaveChanges to SalesDbContext.
/// </summary>
internal sealed class SalesUnitOfWork(SalesDbContext dbContext) : ISalesUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => dbContext.SaveChangesAsync(cancellationToken);
}
