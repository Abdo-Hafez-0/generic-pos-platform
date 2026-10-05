using Suppliers.Contracts.Models;

namespace Suppliers.Contracts.Interfaces;

/// <summary>
/// Lets other modules (POS, Pricing, Sales ...) identify a supplier by ID or code without knowing how suppliers are stored.
/// Implemented by Suppliers.Infrastructure.Services.SupplierLookup.
/// </summary>
public interface ISupplierLookup
{
    Task<SupplierLookupResult?> FindByIdAsync(Guid supplierId, CancellationToken cancellationToken = default);

    Task<SupplierLookupResult?> FindByCodeAsync(string code, CancellationToken cancellationToken = default);
}

/// <summary>Read-oriented API for searching suppliers and for reporting. Implemented by Suppliers.Infrastructure.Services.SupplierReader.</summary>
public interface ISupplierReader
{
    Task<IReadOnlyList<SupplierLookupResult>> SearchAsync(string text, int limit = 25, CancellationToken cancellationToken = default);

    Task<SupplierSummaryResult> GetSummaryAsync(CancellationToken cancellationToken = default);
}
