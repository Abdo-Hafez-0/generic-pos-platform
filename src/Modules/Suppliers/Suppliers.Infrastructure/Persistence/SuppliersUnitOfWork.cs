using Suppliers.Application.Abstractions;

namespace Suppliers.Infrastructure.Persistence;

internal sealed class SuppliersUnitOfWork(SuppliersDbContext dbContext) : ISuppliersUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => dbContext.SaveChangesAsync(cancellationToken);
}
