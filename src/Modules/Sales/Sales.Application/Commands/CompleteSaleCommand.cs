using Platform.Core.Results;
using Sales.Application.Abstractions;
using Sales.Application.Repositories;
using Sales.Domain.Entities;
using Sales.Domain.ValueObjects;

namespace Sales.Application.Commands;

// ============================================================
// CompleteSaleCommand
// ============================================================

/// <summary>
/// Completes a Confirmed Sale, transitioning it to Completed status.
///
/// This creates a SalesTransaction record capturing the final monetary totals.
/// In the full vertical slice (Stage 5D+), this handler will also:
///   - Coordinate with Payments (through Payments.Contracts when implemented)
///   - Reduce inventory (through Inventory.Contracts IStockAdjustmentService)
///   - Commit as a single transaction
///
/// For Stage 5C, this establishes the Sale completion and records the SalesTransaction.
/// Inventory reduction is intentionally deferred to Stage 5D (POS vertical slice).
///
/// DESIGN NOTE (Architecture §34, §35):
/// The architecture mandates that cross-module operations (Sale + Payments + Inventory)
/// be coordinated at the use-case/application level. This handler establishes the
/// correct pattern. Stage 5D will extend it with cross-module coordination.
/// </summary>
public sealed record CompleteSaleCommand(
    Guid SaleId,
    string? TransactionReference = null);

public sealed class CompleteSaleCommandHandler(
    ISaleRepository saleRepository,
    ISalesTransactionRepository transactionRepository,
    ISalesUnitOfWork unitOfWork)
{
    public async Task<Result<Guid>> HandleAsync(
        CompleteSaleCommand command,
        CancellationToken cancellationToken = default)
    {
        // 1. Load the sale
        var saleId = new SaleId(command.SaleId);
        var sale = await saleRepository.GetByIdAsync(saleId, cancellationToken);
        if (sale is null)
            return Result.Failure<Guid>(Error.NotFound(
                "Sales.CompleteSale.SaleNotFound",
                $"Sale '{command.SaleId}' was not found."));

        // 2. Complete the sale (transitions Confirmed -> Completed, freezes historical data)
        var completeResult = sale.Complete();
        if (completeResult.IsFailure)
            return Result.Failure<Guid>(completeResult.Error);

        // 3. Record the SalesTransaction (financial record of this sale)
        var transactionResult = SalesTransaction.Record(
            sale.Id,
            sale.GrandTotal,
            sale.TaxTotal,
            command.TransactionReference);

        if (transactionResult.IsFailure)
            return Result.Failure<Guid>(transactionResult.Error);

        saleRepository.Update(sale);
        await transactionRepository.AddAsync(transactionResult.Value, cancellationToken);

        // 4. Persist atomically within Sales module
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(transactionResult.Value.Id);
    }
}
