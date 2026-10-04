using Platform.Core.Results;
using Sales.Application.Abstractions;
using Sales.Application.Repositories;
using Sales.Domain.ValueObjects;

namespace Sales.Application.Commands;

// ============================================================
// CancelSaleCommand
// ============================================================

/// <summary>
/// Cancels a Draft or Confirmed Sale.
/// Completed sales cannot be cancelled.
/// </summary>
public sealed record CancelSaleCommand(Guid SaleId, string Reason);

public sealed class CancelSaleCommandHandler(
    ISaleRepository saleRepository,
    ISalesUnitOfWork unitOfWork)
{
    public async Task<Result> HandleAsync(
        CancelSaleCommand command,
        CancellationToken cancellationToken = default)
    {
        var sale = await saleRepository.GetByIdAsync(new SaleId(command.SaleId), cancellationToken);
        if (sale is null)
            return Result.Failure(Error.NotFound(
                "Sales.CancelSale.SaleNotFound",
                $"Sale '{command.SaleId}' was not found."));

        var cancelResult = sale.Cancel(command.Reason);
        if (cancelResult.IsFailure)
            return cancelResult;

        saleRepository.Update(sale);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
