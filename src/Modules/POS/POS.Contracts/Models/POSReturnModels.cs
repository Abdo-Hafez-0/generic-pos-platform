namespace POS.Contracts.Models;

/// <summary>
/// MISS-10b: a line of a completed sale as the till's return sees it - sold, already returned, what is left, and the money of the line (its
/// total after discount, tax included, and what earlier returns gave back of it).
/// </summary>
public sealed record POSReturnableLine(Guid SaleItemId, string ProductSku, string ProductName, decimal SoldQuantity, decimal ReturnedQuantity,
    decimal LineTotal, decimal RefundedAmount)
{
    public decimal RemainingQuantity => SoldQuantity - ReturnedQuantity;

    /// <summary>
    /// What returning <paramref name="quantity"/> units gives back - the same rule Sales applies when it records the return: the units'
    /// share of the line total, rounded to 2 decimals; the units that empty the line take exactly what is left of it.
    /// </summary>
    public decimal RefundFor(decimal quantity)
        => quantity <= 0m ? 0m
            : quantity >= RemainingQuantity ? LineTotal - RefundedAmount
            : decimal.Round(LineTotal * quantity / SoldQuantity, 2, MidpointRounding.AwayFromZero);
}

/// <summary>
/// MISS-10b: a completed sale the till can take items back from. <see cref="IsLate"/>: older than the shop's limit (<see cref="DaysAllowed"/>),
/// so only someone allowed to accept late returns can give the refund.
/// </summary>
public sealed record POSReturnableSale(Guid SaleId, string ReceiptNumber, DateTime CompletedAtUtc, string? CustomerName, decimal GrandTotal,
    decimal RefundedTotal, IReadOnlyList<POSReturnableLine> Lines, bool IsLate, int DaysAllowed)
{
    public bool HasAnythingLeft => Lines.Any(l => l.RemainingQuantity > 0m);
}

/// <summary>MISS-10b: the sale a receipt number points at, or why none was found (not found, or the number matches several sales).</summary>
public sealed record POSFindReturnResult(bool IsSuccess, POSReturnableSale? Sale, string? ErrorCode, string? ErrorMessage)
{
    public static POSFindReturnResult Success(POSReturnableSale sale) => new(true, sale, null, null);

    public static POSFindReturnResult Failure(string errorCode, string errorMessage) => new(false, null, errorCode, errorMessage);
}

/// <summary>
/// MISS-11a-3 (owner's decision: a short line on the till): electronic receipts that have waited longer than the till's limit to reach the tax
/// authority - how many, since when, and the limit.
/// </summary>
public sealed record POSFiscalNotice(int OverdueCount, DateTime OldestAtUtc, double LimitHours);

/// <summary>
/// MISS-11c (owner's decision: asked at the till): a sale whose total is at or above <see cref="Threshold"/> must name its buyer - a name and a
/// national ID of <see cref="NationalIdLength"/> digits (Egypt's e-receipt: 150,000 EGP, 14 digits).
/// </summary>
public sealed record POSFiscalBuyerRule(decimal Threshold, int NationalIdLength)
{
    public bool Applies(decimal total) => total >= Threshold;

    public bool IsSatisfiedBy(POSBuyer? buyer)
        => buyer is not null && !string.IsNullOrWhiteSpace(buyer.Name) && buyer.NationalId?.Trim() is { } id && id.Length == NationalIdLength && id.All(char.IsAsciiDigit);
}

/// <summary>MISS-11c: the buyer of a sale as the tax authority's receipt names them.</summary>
public sealed record POSBuyer(string Name, string NationalId);

/// <summary>MISS-10b: one line to give back: the sale line, the units, and whether they go back on the shelf (false = damaged).</summary>
public sealed record POSRefundLineRequest(Guid SaleItemId, decimal Quantity, bool BackToStock = true);

/// <summary>
/// MISS-10b: a refund given (the return recorded, the money given back), or why it was refused (nothing was saved). MISS-10c:
/// <see cref="HardwareNotices"/> what the receipt printer or the drawer could not do (the refund stands).
/// </summary>
public sealed record POSRefundResult(bool IsSuccess, Guid ReturnId, decimal RefundTotal, string? ErrorCode, string? ErrorMessage,
    IReadOnlyList<POSHardwareNotice>? HardwareNotices = null)
{
    public static POSRefundResult Success(Guid returnId, decimal refundTotal, IReadOnlyList<POSHardwareNotice>? hardwareNotices = null)
        => new(true, returnId, refundTotal, null, null, hardwareNotices);

    public static POSRefundResult Failure(string errorCode, string errorMessage) => new(false, Guid.Empty, 0m, errorCode, errorMessage);
}
