using Platform.Core.Results;
using Sales.Domain.ValueObjects;

namespace Sales.Domain.Entities;

/// <summary>
/// SalesTransaction — records the financial record of a completed Sale.
///
/// This entity captures the finalised monetary totals of a Sale at the moment
/// of completion. It exists as the finance-facing record of the transaction.
///
/// Unlike Sale (which owns the item details), SalesTransaction records:
///   - The final grand total at completion
///   - The tax collected
///   - A reference back to the originating Sale
///
/// DESIGN NOTE:
/// SalesTransaction is intentionally minimal at this stage. In the full vertical
/// slice (Stage 5D+), this will be expanded to link with Payments when the
/// CompleteSaleHandler is implemented. At that point, the transaction will
/// record payment method, amounts, etc.
///
/// INVARIANTS:
/// - Must reference a valid Sale.
/// - GrandTotal must be non-negative.
/// - TaxTotal must be non-negative.
/// - TaxTotal cannot exceed GrandTotal.
///
/// Architecture: Sales.Domain — no EF Core, no WPF, no HTTP.
/// Architecture reference: Module Map §17 (SalesTransaction), §34 Database Transactions.
/// </summary>
public sealed class SalesTransaction
{
    private SalesTransaction() { }

    public Guid Id { get; private set; }
    public SaleId SaleId { get; private set; }
    public Money GrandTotal { get; private set; }
    public Money TaxTotal { get; private set; }
    public DateTime TransactedAt { get; private set; }

    /// <summary>Optional external reference (e.g., receipt number).</summary>
    public string? Reference { get; private set; }

    // -----------------------------------------------------------------------
    // Factory
    // -----------------------------------------------------------------------

    public static Result<SalesTransaction> Record(
        SaleId saleId,
        Money grandTotal,
        Money taxTotal,
        string? reference = null)
    {
        if (saleId == SaleId.Empty)
            return Result.Failure<SalesTransaction>(Error.Validation(
                "Sales.SalesTransaction.SaleRequired",
                "SalesTransaction must reference a valid Sale."));

        if (taxTotal > grandTotal)
            return Result.Failure<SalesTransaction>(Error.Validation(
                "Sales.SalesTransaction.TaxExceedsTotal",
                "Tax total cannot exceed grand total."));

        if (reference is not null && reference.Length > 100)
            return Result.Failure<SalesTransaction>(Error.Validation(
                "Sales.SalesTransaction.ReferenceTooLong",
                "Transaction reference cannot exceed 100 characters."));

        return Result.Success(new SalesTransaction
        {
            Id = Guid.NewGuid(),
            SaleId = saleId,
            GrandTotal = grandTotal,
            TaxTotal = taxTotal,
            TransactedAt = DateTime.UtcNow,
            Reference = reference?.Trim()
        });
    }
}
