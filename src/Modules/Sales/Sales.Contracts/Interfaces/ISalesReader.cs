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

    /// <summary>
    /// FIX-12: EVERY sale COMPLETED in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>] (inclusive, UTC), oldest first - no count limit,
    /// so a report over a busy period is never cut off. Reporting limits the range itself (at most 366 days).
    /// </summary>
    Task<IReadOnlyList<CompletedSaleResult>> GetCompletedBetweenAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default);

    /// <summary>MISS-10: a COMPLETED sale with its lines, what was already returned and refunded; null when there is no such completed sale.</summary>
    Task<ReturnableSaleResult?> GetReturnableAsync(Guid saleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// MISS-10: the completed sales a receipt number points at ("S-" and the first 8 characters of the sale's id, as printed; the "S-" may be
    /// left out), newest first - normally one.
    /// </summary>
    Task<IReadOnlyList<Guid>> FindCompletedByReceiptNumberAsync(string receiptNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// MISS-10c: every refund GIVEN in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>] (inclusive, UTC; the processed returns), oldest
    /// first - a refund counts on the day it was given, whatever the day of its sale.
    /// </summary>
    Task<IReadOnlyList<RefundGivenResult>> GetRefundsBetweenAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default);
}
