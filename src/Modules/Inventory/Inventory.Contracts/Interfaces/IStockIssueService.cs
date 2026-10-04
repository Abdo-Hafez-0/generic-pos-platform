using Inventory.Contracts.Models;

namespace Inventory.Contracts.Interfaces;

/// <summary>
/// Allows other modules (POS) to reduce stock for a sale through a stable contract.
///
/// Implemented by Inventory.Infrastructure.Services.StockIssueService, which delegates to the
/// Inventory application layer. Records a StockOut movement and decreases the balance atomically.
/// Fails (without changing anything) if there is not enough stock.
///
/// Consumed by: POS (Stage 5D).
/// </summary>
public interface IStockIssueService
{
    Task<IssueStockResult> IssueStockAsync(
        Guid catalogProductId,
        Guid warehouseId,
        decimal quantity,
        string? reference = null,
        CancellationToken cancellationToken = default);
}
