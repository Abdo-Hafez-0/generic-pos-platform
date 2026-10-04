using Microsoft.EntityFrameworkCore;
using Sales.Application.Repositories;
using Sales.Domain.Entities;
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
}
