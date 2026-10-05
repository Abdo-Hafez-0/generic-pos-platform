using Customers.Application.Abstractions;

namespace Customers.Infrastructure.Persistence;

internal sealed class CustomersUnitOfWork(CustomersDbContext dbContext) : ICustomersUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => dbContext.SaveChangesAsync(cancellationToken);
}
