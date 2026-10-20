using Catalog.Contracts.Interfaces;
using Catalog.Contracts.Models;
using Catalog.Domain.Enums;
using Catalog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Catalog.Infrastructure.Services;

/// <summary>MISS-11b: implements Catalog.Contracts' listing (read only, nothing tracked).</summary>
internal sealed class CatalogListing(CatalogDbContext dbContext) : ICatalogListing
{
    public async Task<IReadOnlyList<CatalogProductListItem>> ListProductsAsync(bool activeOnly = true, CancellationToken cancellationToken = default)
    {
        var query = dbContext.Products.AsNoTracking().Include(p => p.Barcodes).AsQueryable();
        if (activeOnly) query = query.Where(p => p.Status == ProductStatus.Active);
        var products = await query.ToListAsync(cancellationToken);
        var units = await dbContext.Units.AsNoTracking().ToDictionaryAsync(u => u.Id, cancellationToken);

        return products
            .Select(p =>
            {
                var unit = units.GetValueOrDefault(p.UnitId);
                return new CatalogProductListItem(p.Id.Value, p.Sku, p.Name, p.UnitId.Value, unit?.Name ?? string.Empty, unit?.Abbreviation ?? string.Empty,
                    p.Barcodes.Select(b => b.Value).ToList(), p.Status == ProductStatus.Active);
            })
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<CatalogUnitListItem>> ListUnitsAsync(CancellationToken cancellationToken = default)
        => (await dbContext.Units.AsNoTracking().ToListAsync(cancellationToken))
            .Select(u => new CatalogUnitListItem(u.Id.Value, u.Name, u.Abbreviation, u.IsActive))
            .OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
}
