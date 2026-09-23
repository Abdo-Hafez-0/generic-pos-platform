using Microsoft.EntityFrameworkCore;
using Catalog.Application.Repositories;
using Catalog.Domain.Entities;
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

    public async Task AddAsync(Product product, CancellationToken cancellationToken = default) =>
        await dbContext.Products.AddAsync(product, cancellationToken);

    public void Update(Product product) =>
        dbContext.Products.Update(product);
}
