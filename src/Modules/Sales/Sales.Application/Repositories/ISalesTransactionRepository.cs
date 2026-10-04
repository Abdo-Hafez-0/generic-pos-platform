using Sales.Domain.Entities;
using Sales.Domain.ValueObjects;

namespace Sales.Application.Repositories;

/// <summary>
/// Repository abstraction for SalesTransaction entities.
/// Implemented by Sales.Infrastructure.Repositories.EfSalesTransactionRepository.
/// </summary>
public interface ISalesTransactionRepository
{
    Task<SalesTransaction?> GetBySaleIdAsync(SaleId saleId, CancellationToken cancellationToken = default);
    Task AddAsync(SalesTransaction transaction, CancellationToken cancellationToken = default);
}
