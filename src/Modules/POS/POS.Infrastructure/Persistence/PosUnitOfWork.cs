using POS.Application.Abstractions;

namespace POS.Infrastructure.Persistence;

internal sealed class PosUnitOfWork(POSDbContext dbContext) : IPosUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => dbContext.SaveChangesAsync(cancellationToken);
}
