using Audit.Application.Abstractions;

namespace Audit.Infrastructure.Persistence;

internal sealed class AuditUnitOfWork(AuditDbContext dbContext) : IAuditUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => dbContext.SaveChangesAsync(cancellationToken);
}
