using Catalog.Application.DTOs;
using Catalog.Domain.Entities;
using Catalog.Domain.ValueObjects;

namespace Catalog.Application.Repositories;

/// <summary>
/// Repository abstraction for Product aggregates.
/// Defined in Application layer, implemented by Catalog.Infrastructure.
/// The Application layer must not reference EF Core directly.
/// </summary>
public interface IProductRepository
{
    Task<Product?> GetByIdAsync(ProductId id, CancellationToken cancellationToken = default);
    Task<Product?> GetBySkuAsync(string sku, CancellationToken cancellationToken = default);
    Task<bool> ExistsBySkuAsync(string sku, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Product>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Products whose name or SKU contains <paramref name="search"/> (case-insensitive) or that carry exactly that barcode; every product when
    /// the search is empty. Ordered by name, at most <paramref name="take"/> rows. Inactive and discontinued products only when asked for.
    /// </summary>
    Task<IReadOnlyList<Product>> SearchAsync(string? search, bool includeInactive, int take, CancellationToken cancellationToken = default);
    Task AddAsync(Product product, CancellationToken cancellationToken = default);
    void Update(Product product);
}
