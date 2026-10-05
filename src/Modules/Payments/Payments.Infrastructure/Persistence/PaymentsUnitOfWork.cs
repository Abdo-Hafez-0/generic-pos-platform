using Payments.Application.Abstractions;

namespace Payments.Infrastructure.Persistence;

internal sealed class PaymentsUnitOfWork(PaymentsDbContext dbContext) : IPaymentsUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => dbContext.SaveChangesAsync(cancellationToken);
}
