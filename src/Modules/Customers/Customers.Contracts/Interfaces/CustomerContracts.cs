using Customers.Contracts.Models;

namespace Customers.Contracts.Interfaces;

/// <summary>
/// Lets other modules (POS, Pricing, Sales ...) identify a customer by ID or code without knowing how customers are stored.
/// Implemented by Customers.Infrastructure.Services.CustomerLookup.
/// </summary>
public interface ICustomerLookup
{
    Task<CustomerLookupResult?> FindByIdAsync(Guid customerId, CancellationToken cancellationToken = default);

    Task<CustomerLookupResult?> FindByCodeAsync(string code, CancellationToken cancellationToken = default);
}

/// <summary>Read-oriented API for searching customers and for reporting. Implemented by Customers.Infrastructure.Services.CustomerReader.</summary>
public interface ICustomerReader
{
    Task<IReadOnlyList<CustomerLookupResult>> SearchAsync(string text, int limit = 25, CancellationToken cancellationToken = default);

    Task<CustomerSummaryResult> GetSummaryAsync(CancellationToken cancellationToken = default);
}
