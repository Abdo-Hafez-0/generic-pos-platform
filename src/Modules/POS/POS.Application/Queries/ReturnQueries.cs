using POS.Application.Devices;
using POS.Contracts.Models;
using Sales.Contracts.Interfaces;
using Sales.Contracts.Models;

namespace POS.Application.Queries;

/// <summary>MISS-10b: a completed sale for the till's return, by its id (Sales history's "Return").</summary>
public sealed record GetReturnableSaleQuery(Guid SaleId);

/// <summary>MISS-10b: a completed sale for the till's return, by the receipt number printed on it ("S-1A2B3C4D").</summary>
public sealed record FindReturnableSaleQuery(string ReceiptNumber);

/// <summary>
/// MISS-10b: reads a completed sale through Sales.Contracts for the till's return - its lines, what is left to return, and whether it is past
/// the shop's limit (PosReturns:DaysAllowed). Reading needs no permission (like Sales history); giving the refund does (RefundSaleCommandHandler).
/// </summary>
public sealed class ReturnableSaleQueryHandler(ISalesReader salesReader, PosReturnOptions? returnOptions = null, TimeProvider? timeProvider = null)
{
    public async Task<POSReturnableSale?> HandleAsync(GetReturnableSaleQuery query, CancellationToken cancellationToken = default)
        => Map(await salesReader.GetReturnableAsync(query.SaleId, cancellationToken));

    public async Task<POSFindReturnResult> HandleAsync(FindReturnableSaleQuery query, CancellationToken cancellationToken = default)
    {
        var number = query.ReceiptNumber?.Trim() ?? string.Empty;
        var found = number.Length == 0 ? [] : await salesReader.FindCompletedByReceiptNumberAsync(number, cancellationToken);
        switch (found.Count)
        {
            case 0:
                return POSFindReturnResult.Failure("POS.Return.ReceiptNotFound", $"No completed sale has the receipt number '{number}'.");
            case > 1:
                return POSFindReturnResult.Failure("POS.Return.ReceiptAmbiguous",
                    $"The receipt number '{number}' matches {found.Count} sales. Open the sale from Sales history instead.");
        }

        var sale = await HandleAsync(new GetReturnableSaleQuery(found[0]), cancellationToken);
        return sale is null
            ? POSFindReturnResult.Failure("POS.Return.ReceiptNotFound", $"No completed sale has the receipt number '{number}'.")
            : POSFindReturnResult.Success(sale);
    }

    /// <summary>The number printed on the sale's receipt (as the receipt handlers print it).</summary>
    public static string ReceiptNumberOf(Guid saleId) => "S-" + saleId.ToString("N")[..8].ToUpperInvariant();

    private POSReturnableSale? Map(ReturnableSaleResult? sale)
    {
        if (sale is null) return null;

        var days = Math.Max(0, (returnOptions ?? new PosReturnOptions()).DaysAllowed);
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        return new POSReturnableSale(sale.SaleId, ReceiptNumberOf(sale.SaleId), sale.CompletedAtUtc, sale.CustomerName, sale.GrandTotal, sale.RefundedTotal,
            sale.Lines.Select(l => new POSReturnableLine(l.SaleItemId, l.ProductSku, l.ProductName, l.SoldQuantity, l.ReturnedQuantity, l.LineTotal, l.RefundedAmount)).ToList(),
            IsLate: now - sale.CompletedAtUtc > TimeSpan.FromDays(days), DaysAllowed: days);
    }
}
