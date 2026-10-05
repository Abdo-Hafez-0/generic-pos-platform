using Suppliers.Application.Repositories;
using Suppliers.Contracts.Interfaces;
using Suppliers.Contracts.Models;
using Suppliers.Domain.Entities;
using Suppliers.Domain.Enums;
using Suppliers.Domain.ValueObjects;

namespace Suppliers.Infrastructure.Services;

internal static class SupplierContractMapping
{
    public static SupplierLookupResult ToLookup(this Supplier c) => new(
        c.Id.Value, c.Code, c.Name, c.Email, c.Phone,
        c.Status == SupplierStatus.Active ? SupplierStatusContract.Active : SupplierStatusContract.Inactive);
}

/// <summary>Implements ISupplierLookup from Suppliers.Contracts.</summary>
internal sealed class SupplierLookup(ISupplierRepository repository) : ISupplierLookup
{
    public async Task<SupplierLookupResult?> FindByIdAsync(Guid supplierId, CancellationToken cancellationToken = default)
        => (await repository.GetByIdAsync(new SupplierId(supplierId), cancellationToken))?.ToLookup();

    public async Task<SupplierLookupResult?> FindByCodeAsync(string code, CancellationToken cancellationToken = default)
        => string.IsNullOrWhiteSpace(code) ? null : (await repository.GetByCodeAsync(code, cancellationToken))?.ToLookup();
}

/// <summary>Implements ISupplierReader from Suppliers.Contracts (read-only).</summary>
internal sealed class SupplierReader(ISupplierRepository repository) : ISupplierReader
{
    public async Task<IReadOnlyList<SupplierLookupResult>> SearchAsync(string text, int limit = 25, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var found = await repository.SearchAsync(text.Trim(), Math.Clamp(limit, 1, 200), cancellationToken);
        return found.Select(c => c.ToLookup()).ToList();
    }

    public async Task<SupplierSummaryResult> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        var active = await repository.CountAsync(SupplierStatus.Active, cancellationToken);
        var inactive = await repository.CountAsync(SupplierStatus.Inactive, cancellationToken);
        return new SupplierSummaryResult(active + inactive, active, inactive);
    }
}
