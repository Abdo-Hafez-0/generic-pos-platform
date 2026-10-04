using Microsoft.EntityFrameworkCore;
using Sales.Contracts.Interfaces;
using Sales.Contracts.Models;
using Sales.Domain.Enums;
using Sales.Domain.ValueObjects;
using Sales.Infrastructure.Persistence;

namespace Sales.Infrastructure.Services;

/// <summary>
/// Implements ISalesReader — exposes read-only Sales data to other modules through Contracts.
///
/// This service reads from SalesDbContext but never exposes domain entities or EF types.
/// Other modules (POS, Reporting) consume this through ISalesReader from Sales.Contracts.
///
/// Architecture reference: Module Map §24 (POS consumes Sales.Contracts).
/// </summary>
internal sealed class SalesReader(SalesDbContext dbContext) : ISalesReader
{
    public async Task<SaleSummaryResult?> FindByIdAsync(Guid saleId, CancellationToken cancellationToken = default)
    {
        var sale = await dbContext.Sales
            .Include(s => s.Items)
            .FirstOrDefaultAsync(s => s.Id == new SaleId(saleId), cancellationToken);

        if (sale is null)
            return null;

        return new SaleSummaryResult(
            SaleId: sale.Id.Value,
            Status: ToContract(sale.Status),
            Reference: sale.Reference,
            GrandTotal: sale.GrandTotal.Amount,
            ItemCount: sale.Items.Count,
            CreatedAt: sale.CreatedAt,
            CompletedAt: sale.CompletedAt);
    }

    public async Task<IReadOnlyList<SaleSummaryResult>> GetRecentAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var sales = await dbContext.Sales
            .Include(s => s.Items)
            .OrderByDescending(s => s.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return sales.Select(sale => new SaleSummaryResult(
            SaleId: sale.Id.Value,
            Status: ToContract(sale.Status),
            Reference: sale.Reference,
            GrandTotal: sale.GrandTotal.Amount,
            ItemCount: sale.Items.Count,
            CreatedAt: sale.CreatedAt,
            CompletedAt: sale.CompletedAt)).ToList().AsReadOnly();
    }

    private static SaleStatusContract ToContract(SaleStatus status) => status switch
    {
        SaleStatus.Draft => SaleStatusContract.Draft,
        SaleStatus.Confirmed => SaleStatusContract.Confirmed,
        SaleStatus.Completed => SaleStatusContract.Completed,
        SaleStatus.Cancelled => SaleStatusContract.Cancelled,
        _ => SaleStatusContract.Draft
    };
}
