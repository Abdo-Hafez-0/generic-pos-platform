namespace Sales.Contracts.Models;

/// <summary>MISS-10: one line of a return - which sale line, how many units, and whether they go back into stock (false = damaged).</summary>
public sealed record ReturnLineRequest(Guid SaleItemId, decimal Quantity, bool BackToStock = true);

/// <summary>MISS-10: a returned line as recorded: the product, the units, where they go, and the money given back for them.</summary>
public sealed record ReturnedLineResult(Guid SaleItemId, Guid CatalogProductId, string ProductName, decimal Quantity, bool BackToStock, decimal Refund);

/// <summary>MISS-10: the outcome of recording a return against a completed sale.</summary>
public sealed record RecordReturnResult(bool IsSuccess, Guid ReturnId, decimal RefundTotal, IReadOnlyList<ReturnedLineResult> Lines,
    string? ErrorCode, string? ErrorMessage)
{
    public static RecordReturnResult Success(Guid returnId, decimal refundTotal, IReadOnlyList<ReturnedLineResult> lines) =>
        new(true, returnId, refundTotal, lines, null, null);

    public static RecordReturnResult Failure(string errorCode, string errorMessage) =>
        new(false, Guid.Empty, 0m, [], errorCode, errorMessage);
}

/// <summary>MISS-10: a line of a completed sale as a return sees it - sold, already returned, and what is left to return.</summary>
public sealed record ReturnableLineResult(Guid SaleItemId, Guid CatalogProductId, string ProductSku, string ProductName,
    decimal SoldQuantity, decimal ReturnedQuantity, decimal LineTotal, decimal RefundedAmount)
{
    public decimal RemainingQuantity => SoldQuantity - ReturnedQuantity;
}

/// <summary>MISS-10: a completed sale with what can still be returned of it.</summary>
public sealed record ReturnableSaleResult(Guid SaleId, DateTime CompletedAtUtc, string? CustomerName, decimal GrandTotal, decimal RefundedTotal,
    IReadOnlyList<ReturnableLineResult> Lines)
{
    public bool HasAnythingLeft => Lines.Any(l => l.RemainingQuantity > 0m);
}
