using Microsoft.EntityFrameworkCore;
using Catalog.Contracts.Interfaces;
using Catalog.Contracts.Models;
using Catalog.Domain.Entities;
using Catalog.Infrastructure.Persistence;

namespace Catalog.Infrastructure.Services;

/// <summary>
/// Implements IProductBarcodeResolver from Catalog.Contracts.
/// Called by future modules (especially POS) when a barcode is scanned.
/// Never exposes CatalogDbContext or Catalog domain entities to other modules.
/// </summary>
internal sealed class CatalogBarcodeResolver(CatalogDbContext dbContext) : IProductBarcodeResolver
{
    public async Task<ProductLookupResult?> ResolveAsync(
        string barcodeValue,
        CancellationToken cancellationToken = default)
    {
        var normalized = barcodeValue.Trim();
        var barcode = await dbContext.Barcodes
            .FirstOrDefaultAsync(b => b.Value == normalized, cancellationToken);

        if (barcode is null) return null;

        var product = await dbContext.Products
            .FirstOrDefaultAsync(p => p.Id == barcode.ProductId, cancellationToken);

        if (product is null) return null;

        var category = await dbContext.Categories
            .FirstOrDefaultAsync(c => c.Id == product.CategoryId, cancellationToken);

        var unit = await dbContext.Units
            .FirstOrDefaultAsync(u => u.Id == product.UnitId, cancellationToken);

        return new ProductLookupResult(
            product.Id.Value,
            product.Sku,
            product.Name,
            product.Description,
            product.CategoryId.Value,
            category?.Name ?? "Unknown",
            product.UnitId.Value,
            unit?.Name ?? "Unknown",
            unit?.Abbreviation ?? "",
            product.SalePrice,
            product.CostPrice,
            (ProductStatusContract)(int)product.Status);
    }
}
