using Platform.Core.Results;
using Sales.Domain.ValueObjects;

namespace Sales.Domain.Entities;

/// <summary>
/// A single line item within a Return.
///
/// ReturnItems reference the original SaleItem to maintain traceability.
/// The return quantity cannot exceed the original sale quantity.
///
/// Architecture: Sales.Domain — no EF Core, no WPF, no HTTP.
/// </summary>
public sealed class ReturnItem
{
    private ReturnItem() { }

    public ReturnItemId Id { get; private set; }
    public ReturnId ReturnId { get; private set; }

    /// <summary>The original SaleItem this return line references.</summary>
    public SaleItemId OriginalSaleItemId { get; private set; }

    /// <summary>Catalog product ID (carried from original SaleItem for convenience).</summary>
    public Guid CatalogProductId { get; private set; }

    /// <summary>Product name at time of original sale (historical).</summary>
    public string ProductName { get; private set; } = string.Empty;

    /// <summary>Quantity being returned. Must be positive.</summary>
    public SaleQuantity Quantity { get; private set; }

    /// <summary>The unit price from the original sale (for refund calculation).</summary>
    public Money UnitPrice { get; private set; }

    /// <summary>Reason for this particular return line.</summary>
    public string? Reason { get; private set; }

    /// <summary>MISS-10: true when the returned units go back into stock; false when they are written off as damaged.</summary>
    public bool BackToStock { get; private set; } = true;

    /// <summary>MISS-10: the money given back for this line - the share of what the customer paid for it (after discounts, tax included).</summary>
    public Money RefundAmount { get; private set; }

    // -----------------------------------------------------------------------
    // Factory
    // -----------------------------------------------------------------------

    internal static Result<ReturnItem> Create(
        ReturnId returnId,
        SaleItemId originalSaleItemId,
        Guid catalogProductId,
        string productName,
        SaleQuantity quantity,
        Money unitPrice,
        string? reason,
        bool backToStock,
        Money refundAmount)
    {
        if (returnId == ReturnId.Empty)
            return Result.Failure<ReturnItem>(Error.Validation(
                "Sales.ReturnItem.ReturnRequired",
                "ReturnItem must be associated with a valid Return."));

        if (originalSaleItemId == SaleItemId.Empty)
            return Result.Failure<ReturnItem>(Error.Validation(
                "Sales.ReturnItem.OriginalSaleItemRequired",
                "ReturnItem must reference a valid original SaleItem."));

        if (catalogProductId == Guid.Empty)
            return Result.Failure<ReturnItem>(Error.Validation(
                "Sales.ReturnItem.ProductRequired",
                "A valid Catalog product ID must be provided."));

        if (string.IsNullOrWhiteSpace(productName))
            return Result.Failure<ReturnItem>(Error.Validation(
                "Sales.ReturnItem.ProductNameRequired",
                "Product name is required for return records."));

        if (refundAmount.Amount < 0m)
            return Result.Failure<ReturnItem>(Error.Validation(
                "Sales.ReturnItem.RefundNegative",
                "A refund cannot be negative."));

        if (reason is not null && reason.Length > 500)
            return Result.Failure<ReturnItem>(Error.Validation(
                "Sales.ReturnItem.ReasonTooLong",
                "Return reason cannot exceed 500 characters."));

        return Result.Success(new ReturnItem
        {
            Id = ReturnItemId.New(),
            ReturnId = returnId,
            OriginalSaleItemId = originalSaleItemId,
            CatalogProductId = catalogProductId,
            ProductName = productName.Trim(),
            Quantity = quantity,
            UnitPrice = unitPrice,
            Reason = reason?.Trim(),
            BackToStock = backToStock,
            RefundAmount = refundAmount
        });
    }
}
