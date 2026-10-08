using Inventory.Application.Commands;
using Inventory.Contracts.Interfaces;
using Inventory.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace Inventory.Infrastructure.Services;

/// <summary>Error codes of Inventory's contract services (FIX-06).</summary>
internal static class InventoryContractErrors
{
    public const string OperationFailedCode = "Inventory.OperationFailed";
}

/// <summary>
/// Implements IStockReceiptService from Inventory.Contracts by delegating to the AddStock use case. An unexpected failure becomes a failed
/// result with a plain sentence (FIX-06, see <see cref="StockIssueService"/>).
/// </summary>
internal sealed class StockReceiptService(AddStockCommandHandler handler, ILogger<StockReceiptService>? logger = null) : IStockReceiptService
{
    public async Task<ReceiveStockResult> ReceiveStockAsync(
        Guid catalogProductId,
        Guid warehouseId,
        decimal quantity,
        string? reference = null,
        CancellationToken cancellationToken = default)
    {
        Platform.Core.Results.Result<Guid> result;
        try
        {
            result = await handler.ExecuteAsync(
                new AddStockCommand(catalogProductId, warehouseId, quantity, null, reference), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogError(ex, "Receiving stock of product {ProductId} into warehouse {WarehouseId} failed unexpectedly.", catalogProductId, warehouseId);
            return ReceiveStockResult.Failure(InventoryContractErrors.OperationFailedCode, "The stock could not be received and Inventory changed nothing. Try again; if it keeps failing, contact support.");
        }

        return result.IsSuccess
            ? ReceiveStockResult.Success(result.Value)
            : ReceiveStockResult.Failure(result.Error.Code, result.Error.Description);
    }
}
