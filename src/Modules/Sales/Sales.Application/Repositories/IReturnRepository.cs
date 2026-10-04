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
}
