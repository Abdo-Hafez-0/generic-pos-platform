using Platform.Application.Abstractions.Authorization;
using Catalog.Contracts.Interfaces;
using Inventory.Domain.Entities;
using Inventory.Domain.Enums;
using Inventory.Domain.ValueObjects;
using Inventory.Application.Abstractions;
using Inventory.Application.Repositories;
using Platform.Core.Results;

namespace Inventory.Application.Commands;

// ============================================================
// AddStockCommand
// ============================================================

/// <summary>
/// Adds stock for a catalog product in a warehouse.
///
/// FLOW:
///   1. Validate product exists via Catalog.Contracts (IProductLookup) — no Catalog.Domain access.
///   2. Validate warehouse exists and is active.
///   3. Find or create a StockItem for (product, warehouse, location).
///   4. Create a StockMovement(StockIn) fact.
///   5. Increase the InventoryBalance.
///   6. Persist atomically via unit of work.
///
/// Architecture: Inventory.Application uses IProductLookup from Catalog.Contracts.
/// It never references CatalogDbContext, Catalog.Domain, or cat_* tables.
/// </summary>
public sealed record AddStockCommand(
    Guid CatalogProductId,
    Guid WarehouseId,
    decimal Quantity,
    Guid? LocationId = null,
    string? Reference = null);

public sealed class AddStockCommandHandler(
    IProductLookup productLookup,
    IWarehouseRepository warehouseRepository,
    ILocationRepository locationRepository,
    IStockItemRepository stockItemRepository,
    IStockMovementRepository movementRepository,
    IInventoryBalanceRepository balanceRepository,
    IInventoryUnitOfWork unitOfWork,
    IAuthorizationService authorization)
{
    public async Task<Result<Guid>> HandleAsync(
        AddStockCommand command,
        CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(Inventory.Application.Security.InventoryCapabilities.ReceiveStock, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<Guid>(allowed.Error);

        return await ExecuteAsync(command, cancellationToken);
    }

    /// <summary>The same operation WITHOUT the capability check, for trusted calls from other modules through this module's
    /// contracts (they run inside an operation the user was already authorized for). Not reachable from UI or other modules.</summary>
    internal async Task<Result<Guid>> ExecuteAsync(
        AddStockCommand command,
        CancellationToken cancellationToken = default)
    {
        // 1. Validate product exists in Catalog (through Catalog.Contracts only)
        var product = await productLookup.FindByIdAsync(command.CatalogProductId, cancellationToken);
        if (product is null)
            return Result.Failure<Guid>(Error.NotFound(
                "Inventory.AddStock.ProductNotFound",
                $"Catalog product '{command.CatalogProductId}' was not found or is not accessible."));

        // 2. Validate warehouse
        var warehouseId = new WarehouseId(command.WarehouseId);
        var warehouse = await warehouseRepository.GetByIdAsync(warehouseId, cancellationToken);
        if (warehouse is null)
            return Result.Failure<Guid>(Error.NotFound(
                "Inventory.AddStock.WarehouseNotFound",
                $"Warehouse '{command.WarehouseId}' was not found."));

        if (!warehouse.IsActive)
            return Result.Failure<Guid>(Error.Conflict(
                "Inventory.AddStock.WarehouseInactive",
                "Cannot add stock to an inactive warehouse."));

        // 3. Validate optional location
        LocationId? locationId = null;
        if (command.LocationId.HasValue)
        {
            locationId = new LocationId(command.LocationId.Value);
            var location = await locationRepository.GetByIdAsync(locationId.Value, cancellationToken);
            if (location is null)
                return Result.Failure<Guid>(Error.NotFound(
                    "Inventory.AddStock.LocationNotFound",
                    $"Location '{command.LocationId}' was not found."));

            if (!location.IsActive)
                return Result.Failure<Guid>(Error.Conflict(
                    "Inventory.AddStock.LocationInactive",
                    "Cannot add stock to an inactive location."));

            if (location.WarehouseId != warehouseId)
                return Result.Failure<Guid>(Error.Validation(
                    "Inventory.AddStock.LocationWarehouseMismatch",
                    "The specified location does not belong to the specified warehouse."));
        }

        // 4. Create quantity value object
        var quantityResult = Quantity.Create(command.Quantity);
        if (quantityResult.IsFailure)
            return Result.Failure<Guid>(quantityResult.Error);

        var quantity = quantityResult.Value;
        if (quantity <= Quantity.Zero)
            return Result.Failure<Guid>(Error.Validation(
                "Inventory.AddStock.QuantityMustBePositive",
                "Stock quantity to add must be greater than zero."));

        // 5. Find or create StockItem
        var stockItem = await stockItemRepository.FindByProductAndWarehouseAsync(
            command.CatalogProductId, warehouseId, cancellationToken);

        bool isNewStockItem = stockItem is null;
        if (isNewStockItem)
        {
            var stockItemResult = StockItem.Create(command.CatalogProductId, warehouseId, locationId);
            if (stockItemResult.IsFailure)
                return Result.Failure<Guid>(stockItemResult.Error);

            stockItem = stockItemResult.Value;
            await stockItemRepository.AddAsync(stockItem, cancellationToken);
        }
        else if (!stockItem!.IsActive)
        {
            return Result.Failure<Guid>(Error.Conflict(
                "Inventory.AddStock.StockItemInactive",
                "Cannot add stock to an inactive stock item."));
        }

        // 6. Record the StockMovement fact
        var movementResult = StockMovement.Record(
            stockItem.Id, MovementType.StockIn, quantity, command.Reference);
        if (movementResult.IsFailure)
            return Result.Failure<Guid>(movementResult.Error);

        await movementRepository.AddAsync(movementResult.Value, cancellationToken);

        // 7. Update or create InventoryBalance
        var balance = await balanceRepository.GetByStockItemAsync(stockItem.Id, cancellationToken);
        if (balance is null)
        {
            var balanceResult = InventoryBalance.Create(stockItem.Id);
            if (balanceResult.IsFailure)
                return Result.Failure<Guid>(balanceResult.Error);

            balance = balanceResult.Value;
            var increaseResult = balance.Increase(quantity);
            if (increaseResult.IsFailure) return Result.Failure<Guid>(increaseResult.Error);

            await balanceRepository.AddAsync(balance, cancellationToken);
        }
        else
        {
            var increaseResult = balance.Increase(quantity);
            if (increaseResult.IsFailure) return Result.Failure<Guid>(increaseResult.Error);

            balanceRepository.Update(balance);
        }

        // 8. Persist atomically
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(movementResult.Value.Id.Value);
    }
}
