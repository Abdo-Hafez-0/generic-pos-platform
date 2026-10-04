using Sales.Application.Commands;
using Sales.Contracts.Interfaces;
using Sales.Contracts.Models;

namespace Sales.Infrastructure.Services;

/// <summary>
/// Implements ISalesService — allows other modules (POS) to drive sale operations
/// through a stable contract without depending on Sales.Application directly.
///
/// This service wraps command handlers and translates their Result types into
/// contract-level result types.
///
/// Architecture reference: Module Map §24 (POS → Sales.Contracts → Sales).
/// </summary>
internal sealed class SalesService(
    CreateSaleCommandHandler createHandler,
    AddSaleItemCommandHandler addItemHandler,
    ConfirmSaleCommandHandler confirmHandler,
    CompleteSaleCommandHandler completeHandler,
    CancelSaleCommandHandler cancelHandler) : ISalesService
{
    public async Task<CreateSaleResult> CreateSaleAsync(
        string? reference = null,
        string? notes = null,
        CancellationToken cancellationToken = default)
    {
        var result = await createHandler.HandleAsync(
            new CreateSaleCommand(reference, notes), cancellationToken);

        return result.IsSuccess
            ? CreateSaleResult.Success(result.Value)
            : CreateSaleResult.Failure(result.Error.Code, result.Error.Description);
    }

    public async Task<AddSaleItemResult> AddItemAsync(
        Guid saleId,
        Guid catalogProductId,
        decimal quantity,
        decimal unitPrice,
        decimal discount = 0m,
        decimal taxRate = 0m,
        Guid? warehouseId = null,
        CancellationToken cancellationToken = default)
    {
        var result = await addItemHandler.HandleAsync(
            new AddSaleItemCommand(saleId, catalogProductId, quantity, unitPrice, discount, taxRate, warehouseId),
            cancellationToken);

        return result.IsSuccess
            ? AddSaleItemResult.Success(result.Value)
            : AddSaleItemResult.Failure(result.Error.Code, result.Error.Description);
    }

    public async Task<SaleOperationResult> ConfirmSaleAsync(
        Guid saleId,
        CancellationToken cancellationToken = default)
    {
        var result = await confirmHandler.HandleAsync(
            new ConfirmSaleCommand(saleId), cancellationToken);

        return result.IsSuccess
            ? SaleOperationResult.Success()
            : SaleOperationResult.Failure(result.Error.Code, result.Error.Description);
    }

    public async Task<SaleOperationResult> CompleteSaleAsync(
        Guid saleId,
        string? transactionReference = null,
        CancellationToken cancellationToken = default)
    {
        var result = await completeHandler.HandleAsync(
            new CompleteSaleCommand(saleId, transactionReference), cancellationToken);

        return result.IsSuccess
            ? SaleOperationResult.Success()
            : SaleOperationResult.Failure(result.Error.Code, result.Error.Description);
    }

    public async Task<SaleOperationResult> CancelSaleAsync(
        Guid saleId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var result = await cancelHandler.HandleAsync(
            new CancelSaleCommand(saleId, reason), cancellationToken);

        return result.IsSuccess
            ? SaleOperationResult.Success()
            : SaleOperationResult.Failure(result.Error.Code, result.Error.Description);
    }
}
