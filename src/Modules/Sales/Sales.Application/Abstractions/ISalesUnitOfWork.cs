namespace Sales.Application.Abstractions;

/// <summary>
/// Unit of Work abstraction for the Sales module.
/// Saves changes to the SalesDbContext.
/// Implemented by Sales.Infrastructure.Persistence.SalesUnitOfWork.
/// </summary>
public interface ISalesUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
