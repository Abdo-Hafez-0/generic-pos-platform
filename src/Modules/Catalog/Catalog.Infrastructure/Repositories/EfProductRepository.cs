using Microsoft.EntityFrameworkCore;
using Catalog.Application.Repositories;
using Catalog.Domain.Entities;
using Catalog.Domain.Enums;
using Catalog.Domain.ValueObjects;
using Catalog.Infrastructure.Persistence;

namespace Catalog.Infrastructure.Repositories;

internal sealed class EfProductRepository(CatalogDbContext dbContext) : IProductRepository
{
    public async Task<Product?> GetByIdAsync(ProductId id, CancellationToken cancellationToken = default) =>
        await dbContext.Products
            .Include(p => p.Barcodes)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public async Task<Product?> GetBySkuAsync(string sku, CancellationToken cancellationToken = default) =>
        await dbContext.Products
            .Include(p => p.Barcodes)
            .FirstOrDefaultAsync(p => p.Sku == sku.Trim().ToUpperInvariant(), cancellationToken);

    public async Task<bool> ExistsBySkuAsync(string sku, CancellationToken cancellationToken = default) =>
        await dbContext.Products
            .AnyAsync(p => p.Sku == sku.Trim().ToUpperInvariant(), cancellationToken);

    public async Task<IReadOnlyList<Product>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await dbContext.Products
            .Include(p => p.Barcodes)
            .OrderBy(p => p.Name)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Product>> SearchAsync(string? search, bool includeInactive, int take, CancellationToken cancellationToken = default)
    {
        IQueryable<Product> query = dbContext.Products.Include(p => p.Barcodes);
        if (!includeInactive)
            query = query.Where(p => p.Status == ProductStatus.Active);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            // the search text is literal: escape LIKE wildcards (SQLite LIKE is case-insensitive for ASCII)
            var pattern = "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(p => EF.Functions.Like(p.Name, pattern, "\\")
                || EF.Functions.Like(p.Sku, pattern, "\\")
                || p.Barcodes.Any(b => b.Value == text));
        }

        return await query.OrderBy(p => p.Name).Take(take).ToListAsync(cancellationToken);
    }

    public async Task AddAsync(Product product, CancellationToken cancellationToken = default) =>
        await dbContext.Products.AddAsync(product, cancellationToken);

    public void Update(Product product) =>
        dbContext.Products.Update(product);
}
