using CashManagement.Application.Abstractions;

namespace CashManagement.Infrastructure.Persistence;

internal sealed class CashManagementUnitOfWork(CashManagementDbContext dbContext) : ICashManagementUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => dbContext.SaveChangesAsync(cancellationToken);
}
