using Purchasing.Application.Abstractions;

namespace Purchasing.Infrastructure.Persistence;

internal sealed class PurchasingUnitOfWork(PurchasingDbContext dbContext) : IPurchasingUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => dbContext.SaveChangesAsync(cancellationToken);
}
