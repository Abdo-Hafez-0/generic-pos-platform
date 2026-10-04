using Microsoft.EntityFrameworkCore;
using Sales.Application.Repositories;
using Sales.Domain.Entities;
using Sales.Domain.ValueObjects;
using Sales.Infrastructure.Persistence;

namespace Sales.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of ISalesTransactionRepository.
/// Internal — only accessible within Sales.Infrastructure (and Sales.Tests via InternalsVisibleTo).
/// </summary>
internal sealed class EfSalesTransactionRepository(SalesDbContext dbContext) : ISalesTransactionRepository
{
    public async Task<SalesTransaction?> GetBySaleIdAsync(SaleId saleId, CancellationToken cancellationToken = default)
        => await dbContext.SalesTransactions
            .FirstOrDefaultAsync(t => t.SaleId == saleId, cancellationToken);

    public async Task AddAsync(SalesTransaction transaction, CancellationToken cancellationToken = default)
        => await dbContext.SalesTransactions.AddAsync(transaction, cancellationToken);
}
