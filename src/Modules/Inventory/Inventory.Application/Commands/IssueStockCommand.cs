using Inventory.Application.Abstractions;
using Inventory.Application.Repositories;
using Inventory.Domain.Entities;
using Inventory.Domain.Enums;
using Inventory.Domain.ValueObjects;
using Platform.Core.Results;

namespace Inventory.Application.Commands;

// ============================================================
// IssueStockCommand
// ============================================================

/// <summary>
/// Issues (removes) stock for a product in a warehouse, e.g. because it was sold.
/// Records a StockOut movement and decreases the InventoryBalance in one unit of work.
/// </summary>
public sealed record IssueStockCommand(
    Guid CatalogProductId,
    Guid WarehouseId,
    decimal Quantity,
    string? Reference = null);

public sealed class IssueStockCommandHandler(
    IStockItemRepository stockItemRepository,
    IStockMovementRepository movementRepository,
    IInventoryBalanceRepository balanceRepository,
    IInventoryUnitOfWork unitOfWork)
{
    public async Task<Result<Guid>> HandleAsync(
        IssueStockCommand command,
        CancellationToken cancellationToken = default)
    {
        var quantityResult = Quantity.Create(command.Quantity);
        if (quantityResult.IsFailure)
            return Result.Failure<Guid>(quantityResult.Error);

        var quantity = quantityResult.Value;
        if (quantity <= Quantity.Zero)
            return Result.Failure<Guid>(Error.Validation(
                "Inventory.IssueStock.QuantityMustBePositive",
                "Stock quantity to issue must be greater than zero."));

        var stockItem = await stockItemRepository.FindByProductAndWarehouseAsync(
            command.CatalogProductId, new WarehouseId(command.WarehouseId), cancellationToken);
        if (stockItem is null)
            return Result.Failure<Guid>(Error.NotFound(
                "Inventory.IssueStock.StockItemNotFound",
                $"No stock item exists for product '{command.CatalogProductId}' in warehouse '{command.WarehouseId}'."));

        if (!stockItem.IsActive)
            return Result.Failure<Guid>(Error.Conflict(
                "Inventory.IssueStock.StockItemInactive",
                "Cannot issue stock from an inactive stock item."));

        var balance = await balanceRepository.GetByStockItemAsync(stockItem.Id, cancellationToken);
        if (balance is null)
            return Result.Failure<Guid>(Error.Conflict(
                "Inventory.IssueStock.InsufficientStock",
                "There is no stock on hand for this product."));

        var decreaseResult = balance.Decrease(quantity);
        if (decreaseResult.IsFailure)
            return Result.Failure<Guid>(decreaseResult.Error);

        var movementResult = StockMovement.Record(stockItem.Id, MovementType.StockOut, quantity, command.Reference);
        if (movementResult.IsFailure)
            return Result.Failure<Guid>(movementResult.Error);

        balanceRepository.Update(balance);
        await movementRepository.AddAsync(movementResult.Value, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(movementResult.Value.Id.Value);
    }
}
