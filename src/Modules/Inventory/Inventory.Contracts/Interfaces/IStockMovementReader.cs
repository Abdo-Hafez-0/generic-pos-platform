using Inventory.Contracts.Models;

namespace Inventory.Contracts.Interfaces;

/// <summary>
/// Allows other modules to read historical stock movement records.
///
/// Implemented by Inventory.Infrastructure.Services.StockMovementReader.
/// Consumed by Reporting and Purchasing modules.
///
/// Cross-module contract: read-only, returns DTOs — never exposes Inventory domain types.
/// Architecture reference: Architecture §18.
/// </summary>
public interface IStockMovementReader
{
    /// <summary>Gets all movements for a specific stock item.</summary>
    Task<IReadOnlyList<StockMovementDto>> GetMovementsForStockItemAsync(
        Guid stockItemId,
        CancellationToken cancellationToken = default);

    /// <summary>Gets recent movements across all stock items (for dashboard/audit).</summary>
    Task<IReadOnlyList<StockMovementDto>> GetRecentMovementsAsync(
        int limit = 100,
        CancellationToken cancellationToken = default);
}
