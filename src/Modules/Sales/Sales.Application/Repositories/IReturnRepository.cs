using Sales.Domain.Entities;
using Sales.Domain.ValueObjects;

namespace Sales.Application.Repositories;

/// <summary>
/// Repository abstraction for Return aggregates.
/// Implemented by Sales.Infrastructure.Repositories.EfReturnRepository.
/// </summary>
public interface IReturnRepository
{
    Task<Return?> GetByIdAsync(ReturnId id, CancellationToken cancellationToken = default);
    Task AddAsync(Return returnEntity, CancellationToken cancellationToken = default);
    void Update(Return returnEntity);

    /// <summary>MISS-10: the processed returns of a sale, with their lines (what was already taken back and refunded).</summary>
    Task<IReadOnlyList<Return>> GetProcessedForSaleAsync(SaleId saleId, CancellationToken cancellationToken = default);
}
