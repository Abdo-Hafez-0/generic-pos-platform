using Sales.Contracts.Models;

namespace Sales.Contracts.Interfaces;

/// <summary>
/// Allows other modules (POS, Reporting) to read Sales information through a stable contract.
///
/// Implemented by Sales.Infrastructure.Services.SalesReader.
/// Consumed by: POS (Stage 5D), Reporting (future).
///
/// Cross-module contract: never exposes Sales.Domain types or SalesDbContext.
/// Architecture reference: Module Map §24 (POS Dependency), §36 (Reporting).
/// </summary>
public interface ISalesReader
{
    /// <summary>Finds a sale by its ID. Returns null if not found.</summary>
    Task<SaleSummaryResult?> FindByIdAsync(Guid saleId, CancellationToken cancellationToken = default);

    /// <summary>Returns a list of recent sales (most recent first), limited to the given count.</summary>
    Task<IReadOnlyList<SaleSummaryResult>> GetRecentAsync(int limit = 50, CancellationToken cancellationToken = default);
}
