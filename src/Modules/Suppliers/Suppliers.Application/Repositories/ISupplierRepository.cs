using Suppliers.Domain.Entities;
using Suppliers.Domain.Enums;
using Suppliers.Domain.ValueObjects;

namespace Suppliers.Application.Repositories;

public interface ISupplierRepository
{
    /// <summary>Loads a supplier including addresses and contacts.</summary>
    Task<Supplier?> GetByIdAsync(SupplierId id, CancellationToken cancellationToken = default);

    Task<Supplier?> GetByCodeAsync(string code, CancellationToken cancellationToken = default);

    Task AddAsync(Supplier supplier, CancellationToken cancellationToken = default);

    /// <summary>Paged list (name order) WITHOUT loading addresses/contacts.</summary>
    Task<IReadOnlyList<Supplier>> ListAsync(int skip, int take, SupplierStatus? status, CancellationToken cancellationToken = default);

    /// <summary>Case-insensitive match on code, name, email or phone.</summary>
    Task<IReadOnlyList<Supplier>> SearchAsync(string text, int take, CancellationToken cancellationToken = default);

    Task<int> CountAsync(SupplierStatus? status, CancellationToken cancellationToken = default);
}
