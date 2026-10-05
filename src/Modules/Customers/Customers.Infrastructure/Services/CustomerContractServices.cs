using Customers.Application.Repositories;
using Customers.Contracts.Interfaces;
using Customers.Contracts.Models;
using Customers.Domain.Entities;
using Customers.Domain.Enums;
using Customers.Domain.ValueObjects;

namespace Customers.Infrastructure.Services;

internal static class CustomerContractMapping
{
    public static CustomerLookupResult ToLookup(this Customer c) => new(
        c.Id.Value, c.Code, c.Name, c.Email, c.Phone,
        c.Status == CustomerStatus.Active ? CustomerStatusContract.Active : CustomerStatusContract.Inactive);
}

/// <summary>Implements ICustomerLookup from Customers.Contracts.</summary>
internal sealed class CustomerLookup(ICustomerRepository repository) : ICustomerLookup
{
    public async Task<CustomerLookupResult?> FindByIdAsync(Guid customerId, CancellationToken cancellationToken = default)
        => (await repository.GetByIdAsync(new CustomerId(customerId), cancellationToken))?.ToLookup();

    public async Task<CustomerLookupResult?> FindByCodeAsync(string code, CancellationToken cancellationToken = default)
        => string.IsNullOrWhiteSpace(code) ? null : (await repository.GetByCodeAsync(code, cancellationToken))?.ToLookup();
}

/// <summary>Implements ICustomerReader from Customers.Contracts (read-only).</summary>
internal sealed class CustomerReader(ICustomerRepository repository) : ICustomerReader
{
    public async Task<IReadOnlyList<CustomerLookupResult>> SearchAsync(string text, int limit = 25, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var found = await repository.SearchAsync(text.Trim(), Math.Clamp(limit, 1, 200), cancellationToken);
        return found.Select(c => c.ToLookup()).ToList();
    }

    public async Task<CustomerSummaryResult> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        var active = await repository.CountAsync(CustomerStatus.Active, cancellationToken);
        var inactive = await repository.CountAsync(CustomerStatus.Inactive, cancellationToken);
        return new CustomerSummaryResult(active + inactive, active, inactive);
    }
}
