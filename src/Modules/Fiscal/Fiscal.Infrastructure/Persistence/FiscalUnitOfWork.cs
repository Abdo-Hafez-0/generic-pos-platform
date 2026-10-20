using Fiscal.Application.Abstractions;

namespace Fiscal.Infrastructure.Persistence;

internal sealed class FiscalUnitOfWork(FiscalDbContext dbContext) : IFiscalUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => dbContext.SaveChangesAsync(cancellationToken);
}
