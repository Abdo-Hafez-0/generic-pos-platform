using Microsoft.EntityFrameworkCore;
using Catalog.Application.Repositories;
using Catalog.Domain.Entities;
using Catalog.Domain.ValueObjects;
using Catalog.Infrastructure.Persistence;

namespace Catalog.Infrastructure.Repositories;

internal sealed class EfCategoryRepository(CatalogDbContext dbContext) : ICategoryRepository
{
    public async Task<Category?> GetByIdAsync(CategoryId id, CancellationToken cancellationToken = default) =>
        await dbContext.Categories.FindAsync([id], cancellationToken);

    public async Task<IReadOnlyList<Category>> GetAllActiveAsync(CancellationToken cancellationToken = default) =>
        await dbContext.Categories
            .Where(c => c.IsActive)
            .OrderBy(c => c.Name)
            .ToListAsync(cancellationToken);

    public async Task<bool> ExistsAsync(CategoryId id, CancellationToken cancellationToken = default) =>
        await dbContext.Categories.AnyAsync(c => c.Id == id, cancellationToken);

    public async Task AddAsync(Category category, CancellationToken cancellationToken = default) =>
        await dbContext.Categories.AddAsync(category, cancellationToken);

    public void Update(Category category) =>
        dbContext.Categories.Update(category);
}
