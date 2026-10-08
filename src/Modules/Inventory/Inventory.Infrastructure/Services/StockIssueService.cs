using Inventory.Application.Commands;
using Inventory.Contracts.Interfaces;
using Inventory.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace Inventory.Infrastructure.Services;

/// <summary>
/// Implements IStockIssueService from Inventory.Contracts by delegating to the application layer.
///
/// FIX-06: an UNEXPECTED failure (database locked or unavailable, disk error) reaches the calling module as a failed result with a plain
/// sentence, never as an exception or database text; the details go to the log. Inside the caller's transaction it is rolled back with
/// everything else; on its own, the single save of the issue kept nothing.
/// </summary>
internal sealed class StockIssueService(IssueStockCommandHandler handler, ILogger<StockIssueService>? logger = null) : IStockIssueService
{
    public async Task<IssueStockResult> IssueStockAsync(
        Guid catalogProductId,
        Guid warehouseId,
        decimal quantity,
        string? reference = null,
        CancellationToken cancellationToken = default)
    {
        Platform.Core.Results.Result<Guid> result;
        try
        {
            result = await handler.HandleAsync(
                new IssueStockCommand(catalogProductId, warehouseId, quantity, reference), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogError(ex, "Issuing stock of product {ProductId} from warehouse {WarehouseId} failed unexpectedly.", catalogProductId, warehouseId);
            return IssueStockResult.Failure(InventoryContractErrors.OperationFailedCode, "The stock could not be issued and Inventory changed nothing. Try again; if it keeps failing, contact support.");
        }

        return result.IsSuccess
            ? IssueStockResult.Success(result.Value)
            : IssueStockResult.Failure(result.Error.Code, result.Error.Description);
    }
}
