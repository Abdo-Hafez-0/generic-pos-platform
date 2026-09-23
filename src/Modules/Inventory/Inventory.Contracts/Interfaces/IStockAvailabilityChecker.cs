namespace Inventory.Contracts.Interfaces;

/// <summary>
/// Allows other modules to check whether sufficient stock is available before
/// committing a sale or reservation.
///
/// Implemented by Inventory.Infrastructure.Services.StockAvailabilityChecker.
/// Consumed by Sales, POS modules.
///
/// Cross-module contract: read-only availability check, no state mutation.
/// Architecture reference: Architecture §18.
/// </summary>
public interface IStockAvailabilityChecker
{
    /// <summary>
    /// Returns true if the on-hand quantity for a catalog product in the given warehouse
    /// is greater than or equal to requiredQuantity.
    /// Returns false if no stock item is registered or stock is insufficient.
    /// </summary>
    Task<bool> IsAvailableAsync(
        Guid catalogProductId,
        Guid warehouseId,
        decimal requiredQuantity,
        CancellationToken cancellationToken = default);
}
