using Microsoft.EntityFrameworkCore;
using Sales.Application.Repositories;
using Sales.Domain.Entities;
using Sales.Domain.ValueObjects;
using Sales.Infrastructure.Persistence;

namespace Sales.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of ISaleRepository.
/// Internal — only accessible within Sales.Infrastructure (and Sales.Tests via InternalsVisibleTo).
/// </summary>
internal sealed class EfSaleRepository(SalesDbContext dbContext) : ISaleRepository
{
    public async Task<Sale?> GetByIdAsync(SaleId id, CancellationToken cancellationToken = default)
        => await dbContext.Sales
            .Include(s => s.Items)
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    public async Task AddAsync(Sale sale, CancellationToken cancellationToken = default)
        => await dbContext.Sales.AddAsync(sale, cancellationToken);

    public void Update(Sale sale)
        => dbContext.Sales.Update(sale);

    public async Task<IReadOnlyList<Sale>> GetAllAsync(CancellationToken cancellationToken = default)
        => await dbContext.Sales
            .Include(s => s.Items)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Sale>> GetCreatedBetweenAsync(DateTime fromUtc, DateTime toUtc, int take, CancellationToken cancellationToken = default)
        => await dbContext.Sales
            .Include(s => s.Items)
            .Where(s => s.CreatedAt >= fromUtc && s.CreatedAt < toUtc)
            .OrderByDescending(s => s.CreatedAt)
            .Take(take)
            .ToListAsync(cancellationToken);
}
