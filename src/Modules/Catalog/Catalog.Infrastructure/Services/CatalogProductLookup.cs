using Microsoft.EntityFrameworkCore;
using Catalog.Contracts.Interfaces;
using Catalog.Contracts.Models;
using Catalog.Domain.Entities;
using Catalog.Domain.ValueObjects;
using Catalog.Infrastructure.Persistence;

namespace Catalog.Infrastructure.Services;

/// <summary>
/// Implements IProductLookup from Catalog.Contracts.
/// This is the cross-module boundary implementation.
/// Other modules (Inventory, Sales, POS) consume this through the IProductLookup interface.
/// They never access CatalogDbContext or any Catalog domain entity.
/// </summary>
internal sealed class CatalogProductLookup(CatalogDbContext dbContext) : IProductLookup
{
    public async Task<ProductLookupResult?> FindByIdAsync(
        Guid productId,
        CancellationToken cancellationToken = default)
    {
        var productIdObj = new ProductId(productId);
        var product = await dbContext.Products
            .Include(p => p.Barcodes)
            .FirstOrDefaultAsync(p => p.Id == productIdObj, cancellationToken);

        return product is null ? null : await MapToResultAsync(product, cancellationToken);
    }

    public async Task<ProductLookupResult?> FindBySkuAsync(
        string sku,
        CancellationToken cancellationToken = default)
    {
        var normalized = sku.Trim().ToUpperInvariant();
        var product = await dbContext.Products
            .Include(p => p.Barcodes)
            .FirstOrDefaultAsync(p => p.Sku == normalized, cancellationToken);

        return product is null ? null : await MapToResultAsync(product, cancellationToken);
    }

    private async Task<ProductLookupResult> MapToResultAsync(
        Product product,
        CancellationToken cancellationToken)
    {
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
