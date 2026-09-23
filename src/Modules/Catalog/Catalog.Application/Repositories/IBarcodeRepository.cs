using Catalog.Domain.Entities;
using Catalog.Domain.ValueObjects;

namespace Catalog.Application.Repositories;

/// <summary>
/// Repository abstraction for barcode lookups.
/// Barcodes are owned by Products, but a dedicated lookup is needed for fast barcode resolution.
/// </summary>
public interface IBarcodeRepository
{
    /// <summary>Finds the product that owns the given barcode value.</summary>
    Task<Product?> GetProductByBarcodeValueAsync(string barcodeValue, CancellationToken cancellationToken = default);
}
