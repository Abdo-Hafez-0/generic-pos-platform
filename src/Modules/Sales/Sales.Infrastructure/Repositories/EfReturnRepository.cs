using Microsoft.EntityFrameworkCore;
using Sales.Application.Repositories;
using Sales.Domain.Entities;
using Sales.Domain.Enums;
using Sales.Domain.ValueObjects;
using Sales.Infrastructure.Persistence;

namespace Sales.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of IReturnRepository.
/// Internal — only accessible within Sales.Infrastructure (and Sales.Tests via InternalsVisibleTo).
/// </summary>
internal sealed class EfReturnRepository(SalesDbContext dbContext) : IReturnRepository
{
    public async Task<Return?> GetByIdAsync(ReturnId id, CancellationToken cancellationToken = default)
        => await dbContext.Returns
            .Include(r => r.Items)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task AddAsync(Return returnEntity, CancellationToken cancellationToken = default)
        => await dbContext.Returns.AddAsync(returnEntity, cancellationToken);

    public void Update(Return returnEntity)
        => dbContext.Returns.Update(returnEntity);

    public async Task<IReadOnlyList<Return>> GetProcessedForSaleAsync(SaleId saleId, CancellationToken cancellationToken = default)
        => await dbContext.Returns
            .Include(r => r.Items)
            .Where(r => r.OriginalSaleId == saleId && r.Status == ReturnStatus.Processed)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Return>> GetProcessedForSalesAsync(IReadOnlyCollection<SaleId> saleIds, CancellationToken cancellationToken = default)
    {
        if (saleIds.Count == 0) return [];
        var found = new List<Return>();
        foreach (var chunk in saleIds.Distinct().Chunk(500))   // keeps the SQL parameter list short
            found.AddRange(await dbContext.Returns.AsNoTracking()
                .Include(r => r.Items)
                .Where(r => chunk.Contains(r.OriginalSaleId) && r.Status == ReturnStatus.Processed)
                .ToListAsync(cancellationToken));
        return found;
    }
}
