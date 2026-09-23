using Catalog.Contracts.Models;

namespace Catalog.Contracts.Interfaces;

/// <summary>
/// Allows other modules to look up product information by ID or SKU.
///
/// Implemented by Catalog.Infrastructure.Services.CatalogProductLookup.
/// Consumed by future modules: Inventory, Sales, POS.
///
/// Cross-module contract: never exposes Catalog.Domain types.
/// Architecture reference: Module Map §10.
/// </summary>
public interface IProductLookup
{
    /// <summary>Finds a product by its ID. Returns null if not found.</summary>
    Task<ProductLookupResult?> FindByIdAsync(Guid productId, CancellationToken cancellationToken = default);

    /// <summary>Finds a product by its SKU. Returns null if not found.</summary>
    Task<ProductLookupResult?> FindBySkuAsync(string sku, CancellationToken cancellationToken = default);
}
