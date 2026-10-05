using Inventory.Application.Commands;
using Inventory.Contracts.Interfaces;
using Inventory.Contracts.Models;

namespace Inventory.Infrastructure.Services;

/// <summary>Implements IStockReceiptService from Inventory.Contracts by delegating to the AddStock use case.</summary>
internal sealed class StockReceiptService(AddStockCommandHandler handler) : IStockReceiptService
{
    public async Task<ReceiveStockResult> ReceiveStockAsync(
        Guid catalogProductId,
        Guid warehouseId,
        decimal quantity,
        string? reference = null,
        CancellationToken cancellationToken = default)
    {
        var result = await handler.HandleAsync(
            new AddStockCommand(catalogProductId, warehouseId, quantity, null, reference), cancellationToken);

        return result.IsSuccess
            ? ReceiveStockResult.Success(result.Value)
            : ReceiveStockResult.Failure(result.Error.Code, result.Error.Description);
    }
}
