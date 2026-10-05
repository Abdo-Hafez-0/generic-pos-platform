namespace Customers.Application.Abstractions;

/// <summary>Saves changes to the Customers module's own persistence (CustomersDbContext).</summary>
public interface ICustomersUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
