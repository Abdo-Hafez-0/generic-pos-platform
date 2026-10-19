using Sales.Application.DTOs;
using Sales.Application.Repositories;
using Sales.Domain.Enums;
using Sales.Domain.ValueObjects;

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

/// <summary>
/// The listed sales and their totals. MISS-10c: <see cref="RefundedBySale"/> what was refunded of each listed sale (by any return, whenever it
/// was given), <see cref="ReturnedByItem"/> how many units of each sale line came back, <see cref="RefundedTotal"/> the refunds of the
/// completed sales listed (so <see cref="NetTotal"/> is what they kept).
/// </summary>
public sealed record SalesHistory(IReadOnlyList<SaleDto> Sales, int CompletedCount, decimal CompletedTotal, bool IsTruncated)
{
    public IReadOnlyDictionary<Guid, decimal> RefundedBySale { get; init; } = new Dictionary<Guid, decimal>();
    public IReadOnlyDictionary<Guid, decimal> ReturnedByItem { get; init; } = new Dictionary<Guid, decimal>();
    public decimal RefundedTotal { get; init; }
    public decimal NetTotal => CompletedTotal - RefundedTotal;
}

public sealed class GetSalesHistoryQueryHandler(ISaleRepository saleRepository, IReturnRepository? returnRepository = null)
{
    public async Task<SalesHistory> HandleAsync(GetSalesHistoryQuery query, CancellationToken cancellationToken = default)
    {
        if (query.ToUtc <= query.FromUtc)
            return new SalesHistory([], 0, 0m, false);

        var sales = await saleRepository.GetCreatedBetweenAsync(query.FromUtc, query.ToUtc, GetSalesHistoryQuery.MaxResults + 1, cancellationToken);
        var shown = sales.Take(GetSalesHistoryQuery.MaxResults).Select(SaleDtoMapper.ToDto).ToList();
        var completed = shown.Where(s => s.Status == SaleStatus.Completed).ToList();

        var returns = returnRepository is null || completed.Count == 0
            ? []
            : await returnRepository.GetProcessedForSalesAsync(completed.Select(s => new SaleId(s.SaleId)).ToList(), cancellationToken);
        var refunded = returns.GroupBy(r => r.OriginalSaleId.Value).ToDictionary(g => g.Key, g => g.Sum(r => r.TotalRefundAmount.Amount));
        var returned = returns.SelectMany(r => r.Items).GroupBy(i => i.OriginalSaleItemId.Value).ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity.Value));

        return new SalesHistory(shown, completed.Count, completed.Sum(s => s.GrandTotal), sales.Count > GetSalesHistoryQuery.MaxResults)
        {
            RefundedBySale = refunded,
            ReturnedByItem = returned,
            RefundedTotal = refunded.Values.Sum(),
        };
    }
}
