using Catalog.Contracts.Models;

namespace Catalog.Contracts.Interfaces;

/// <summary>
/// Resolves a barcode string to a product.
///
/// Implemented by Catalog.Infrastructure.Services.CatalogBarcodeResolver.
/// Consumed by future modules: POS (scan barcode to add item to cart).
///
/// Cross-module contract: never exposes Catalog.Domain types.
/// Architecture reference: Module Map §10.
/// </summary>
public interface IProductBarcodeResolver
{
    /// <summary>
    /// Resolves a barcode value to a product. Returns null if no product has this barcode.
    /// </summary>
    Task<ProductLookupResult?> ResolveAsync(string barcodeValue, CancellationToken cancellationToken = default);
}
