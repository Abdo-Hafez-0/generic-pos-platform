using Sales.Domain.Entities;
using Sales.Domain.ValueObjects;

namespace Sales.Application.Repositories;

/// <summary>
/// Repository abstraction for Sale aggregates.
/// Implemented by Sales.Infrastructure.Repositories.EfSaleRepository.
/// </summary>
public interface ISaleRepository
{
    Task<Sale?> GetByIdAsync(SaleId id, CancellationToken cancellationToken = default);
    Task AddAsync(Sale sale, CancellationToken cancellationToken = default);
    void Update(Sale sale);
    Task<IReadOnlyList<Sale>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Sales created in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>), newest first, at most <paramref name="take"/>.</summary>
    Task<IReadOnlyList<Sale>> GetCreatedBetweenAsync(DateTime fromUtc, DateTime toUtc, int take, CancellationToken cancellationToken = default);
}
