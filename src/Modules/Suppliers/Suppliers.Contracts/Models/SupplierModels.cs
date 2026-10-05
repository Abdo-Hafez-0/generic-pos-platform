namespace Suppliers.Contracts.Models;

public enum SupplierStatusContract
{
    Active = 1,
    Inactive = 2
}

/// <summary>What other modules may know about a supplier. Never exposes Suppliers.Domain types.</summary>
public sealed record SupplierLookupResult(
    Guid SupplierId,
    string Code,
    string Name,
    string? Email,
    string? Phone,
    SupplierStatusContract Status);

/// <summary>Read model used by reporting.</summary>
public sealed record SupplierSummaryResult(int Total, int Active, int Inactive);
