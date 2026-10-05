using Inventory.Contracts.Models;

namespace Inventory.Contracts.Interfaces;

/// <summary>
/// Allows other modules (Purchasing) to receive goods into stock through a stable contract. Inventory remains the only owner of
/// stock: this records a StockIn movement and increases the balance atomically inside Inventory.
///
/// Implemented by Inventory.Infrastructure.Services.StockReceiptService (delegating to the existing AddStock use case).
/// Consumed by: Purchasing (Stage 8C).
/// </summary>
public interface IStockReceiptService
{
    Task<ReceiveStockResult> ReceiveStockAsync(
        Guid catalogProductId,
        Guid warehouseId,
        decimal quantity,
        string? reference = null,
        CancellationToken cancellationToken = default);
}
