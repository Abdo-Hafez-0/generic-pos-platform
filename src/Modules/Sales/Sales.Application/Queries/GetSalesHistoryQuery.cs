using Sales.Application.DTOs;
using Sales.Application.Repositories;
using Sales.Domain.Enums;

namespace Sales.Application.Queries;

// ============================================================
// GetSalesHistoryQuery (FIX-01c: the sales history screen)
// ============================================================

/// <summary>
/// The sales created in [<see cref="FromUtc"/>, <see cref="ToUtc"/>), newest first, at most <see cref="MaxResults"/>; the totals count only
/// COMPLETED sales (drafts and cancelled sales are listed but never counted as takings).
/// </summary>
public sealed record GetSalesHistoryQuery(DateTime FromUtc, DateTime ToUtc)
{
    public const int MaxResults = 1000;
}

public sealed record SalesHistory(IReadOnlyList<SaleDto> Sales, int CompletedCount, decimal CompletedTotal, bool IsTruncated);

public sealed class GetSalesHistoryQueryHandler(ISaleRepository saleRepository)
{
    public async Task<SalesHistory> HandleAsync(GetSalesHistoryQuery query, CancellationToken cancellationToken = default)
    {
        if (query.ToUtc <= query.FromUtc)
            return new SalesHistory([], 0, 0m, false);

        var sales = await saleRepository.GetCreatedBetweenAsync(query.FromUtc, query.ToUtc, GetSalesHistoryQuery.MaxResults + 1, cancellationToken);
        var shown = sales.Take(GetSalesHistoryQuery.MaxResults).Select(SaleDtoMapper.ToDto).ToList();
        var completed = shown.Where(s => s.Status == SaleStatus.Completed).ToList();

        return new SalesHistory(shown, completed.Count, completed.Sum(s => s.GrandTotal), sales.Count > GetSalesHistoryQuery.MaxResults);
    }
}
