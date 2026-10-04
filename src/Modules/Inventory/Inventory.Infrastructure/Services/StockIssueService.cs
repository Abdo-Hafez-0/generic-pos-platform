using Inventory.Application.Commands;
using Inventory.Contracts.Interfaces;
using Inventory.Contracts.Models;

namespace Inventory.Infrastructure.Services;

/// <summary>
/// Implements IStockIssueService from Inventory.Contracts by delegating to the application layer.
/// </summary>
internal sealed class StockIssueService(IssueStockCommandHandler handler) : IStockIssueService
{
    public async Task<IssueStockResult> IssueStockAsync(
        Guid catalogProductId,
        Guid warehouseId,
        decimal quantity,
        string? reference = null,
        CancellationToken cancellationToken = default)
    {
        var result = await handler.HandleAsync(
            new IssueStockCommand(catalogProductId, warehouseId, quantity, reference), cancellationToken);

        return result.IsSuccess
            ? IssueStockResult.Success(result.Value)
            : IssueStockResult.Failure(result.Error.Code, result.Error.Description);
    }
}
