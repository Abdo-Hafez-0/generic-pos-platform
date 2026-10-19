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
            CompletedAt: sale.CompletedAt,
            CustomerId: sale.CustomerId,
            CustomerName: sale.CustomerName);
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
            CompletedAt: sale.CompletedAt,
            CustomerId: sale.CustomerId,
            CustomerName: sale.CustomerName)).ToList().AsReadOnly();
    }

    public async Task<IReadOnlyList<CompletedSaleResult>> GetCompletedBetweenAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default)
    {
        var (from, to) = (AsUtc(fromUtc), AsUtc(toUtc));

        // Totals are computed from the lines (Sale.GrandTotal), so the lines are loaded - read-only, nothing is tracked.
        var sales = await dbContext.Sales.AsNoTracking()
            .Include(s => s.Items)
            .Where(s => s.Status == SaleStatus.Completed && s.CompletedAt != null && s.CompletedAt >= from && s.CompletedAt <= to)
            .OrderBy(s => s.CompletedAt)
            .ToListAsync(cancellationToken);

        return sales.Select(s => new CompletedSaleResult(s.Id.Value, DateTime.SpecifyKind(s.CompletedAt!.Value, DateTimeKind.Utc), s.GrandTotal.Amount, s.TaxTotal.Amount))
            .ToList().AsReadOnly();
    }

    public async Task<ReturnableSaleResult?> GetReturnableAsync(Guid saleId, CancellationToken cancellationToken = default)
    {
        var id = new SaleId(saleId);
        var sale = await dbContext.Sales.AsNoTracking()
            .Include(s => s.Items)
            .FirstOrDefaultAsync(s => s.Id == id && s.Status == SaleStatus.Completed, cancellationToken);
        if (sale is null) return null;

        var returned = await dbContext.Returns.AsNoTracking()
            .Include(r => r.Items)
            .Where(r => r.OriginalSaleId == id && r.Status == ReturnStatus.Processed)
            .SelectMany(r => r.Items)
            .ToListAsync(cancellationToken);

        var lines = sale.Items.Select(item =>
        {
            var back = returned.Where(r => r.OriginalSaleItemId == item.Id).ToList();
            return new ReturnableLineResult(item.Id.Value, item.CatalogProductId, item.ProductSku, item.ProductName, item.Quantity.Value,
                back.Sum(r => r.Quantity.Value), item.LineTotal.Amount, back.Sum(r => r.RefundAmount.Amount));
        }).ToList();

        return new ReturnableSaleResult(sale.Id.Value, DateTime.SpecifyKind(sale.CompletedAt ?? sale.CreatedAt, DateTimeKind.Utc), sale.CustomerName,
            sale.GrandTotal.Amount, lines.Sum(l => l.RefundedAmount), lines);
    }

    public async Task<IReadOnlyList<Guid>> FindCompletedByReceiptNumberAsync(string receiptNumber, CancellationToken cancellationToken = default)
    {
        // The receipt prints "S-" and the first 8 hexadecimal characters of the sale's id (POS); SQLite keeps the id as text.
        var key = (receiptNumber ?? string.Empty).Trim().ToUpperInvariant();
        if (key.StartsWith("S-", StringComparison.Ordinal)) key = key[2..];
        if (key.Length != 8 || !key.All(Uri.IsHexDigit)) return [];

        var pattern = key + "%";
        var sales = await dbContext.Sales.FromSqlInterpolated($"SELECT * FROM sal_Sales WHERE Id LIKE {pattern}")
            .AsNoTracking()
            .Where(s => s.Status == SaleStatus.Completed)
            .OrderByDescending(s => s.CompletedAt)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);
        return sales.Select(s => s.Value).ToList();
    }

    private static DateTime AsUtc(DateTime value)
        => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static SaleStatusContract ToContract(SaleStatus status) => status switch
    {
        SaleStatus.Draft => SaleStatusContract.Draft,
        SaleStatus.Confirmed => SaleStatusContract.Confirmed,
        SaleStatus.Completed => SaleStatusContract.Completed,
        SaleStatus.Cancelled => SaleStatusContract.Cancelled,
        _ => SaleStatusContract.Draft
    };
}
