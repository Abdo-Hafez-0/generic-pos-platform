namespace Customers.Contracts.Models;

public enum CustomerStatusContract
{
    Active = 1,
    Inactive = 2
}

/// <summary>What other modules may know about a customer. Never exposes Customers.Domain types.</summary>
public sealed record CustomerLookupResult(
    Guid CustomerId,
    string Code,
    string Name,
    string? Email,
    string? Phone,
    CustomerStatusContract Status);

/// <summary>Read model used by reporting.</summary>
public sealed record CustomerSummaryResult(int Total, int Active, int Inactive);
