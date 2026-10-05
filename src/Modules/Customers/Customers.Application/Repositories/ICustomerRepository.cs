using Customers.Domain.Entities;
using Customers.Domain.Enums;
using Customers.Domain.ValueObjects;

namespace Customers.Application.Repositories;

public interface ICustomerRepository
{
    /// <summary>Loads a customer including addresses and contacts.</summary>
    Task<Customer?> GetByIdAsync(CustomerId id, CancellationToken cancellationToken = default);

    Task<Customer?> GetByCodeAsync(string code, CancellationToken cancellationToken = default);

    Task AddAsync(Customer customer, CancellationToken cancellationToken = default);

    /// <summary>Paged list (name order) WITHOUT loading addresses/contacts.</summary>
    Task<IReadOnlyList<Customer>> ListAsync(int skip, int take, CustomerStatus? status, CancellationToken cancellationToken = default);

    /// <summary>Case-insensitive match on code, name, email or phone.</summary>
    Task<IReadOnlyList<Customer>> SearchAsync(string text, int take, CancellationToken cancellationToken = default);

    Task<int> CountAsync(CustomerStatus? status, CancellationToken cancellationToken = default);
}
