using Platform.Core.Results;
using Sales.Application.Abstractions;
using Sales.Application.Repositories;
using Sales.Domain.Entities;
using Sales.Domain.ValueObjects;

namespace Sales.Application.Commands;

// ============================================================
// ConfirmSaleCommand
// ============================================================

/// <summary>
/// Confirms a Draft Sale, transitioning it to Confirmed status.
/// A confirmed sale's items cannot be changed.
/// </summary>
public sealed record ConfirmSaleCommand(Guid SaleId);

public sealed class ConfirmSaleCommandHandler(
    ISaleRepository saleRepository,
    ISalesUnitOfWork unitOfWork)
{
    public async Task<Result> HandleAsync(
        ConfirmSaleCommand command,
        CancellationToken cancellationToken = default)
    {
        var sale = await saleRepository.GetByIdAsync(new SaleId(command.SaleId), cancellationToken);
        if (sale is null)
            return Result.Failure(Error.NotFound(
                "Sales.ConfirmSale.SaleNotFound",
                $"Sale '{command.SaleId}' was not found."));

        var confirmResult = sale.Confirm();
        if (confirmResult.IsFailure)
            return confirmResult;

        saleRepository.Update(sale);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
