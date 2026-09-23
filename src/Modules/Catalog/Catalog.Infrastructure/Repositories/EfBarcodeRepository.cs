using Microsoft.EntityFrameworkCore;
using Catalog.Application.Repositories;
using Catalog.Domain.Entities;
using Catalog.Infrastructure.Persistence;

namespace Catalog.Infrastructure.Repositories;

internal sealed class EfBarcodeRepository(CatalogDbContext dbContext) : IBarcodeRepository
{
    public async Task<Product?> GetProductByBarcodeValueAsync(
        string barcodeValue,
        CancellationToken cancellationToken = default)
    {
        var normalized = barcodeValue.Trim();
        var barcode = await dbContext.Barcodes
            .FirstOrDefaultAsync(b => b.Value == normalized, cancellationToken);

        if (barcode is null) return null;

        return await dbContext.Products
            .Include(p => p.Barcodes)
            .FirstOrDefaultAsync(p => p.Id == barcode.ProductId, cancellationToken);
    }
}
