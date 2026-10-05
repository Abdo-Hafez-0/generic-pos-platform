using Platform.Application.Abstractions.Authorization;
using Inventory.Domain.Entities;
using Inventory.Domain.Enums;
using Inventory.Domain.ValueObjects;
using Inventory.Application.Abstractions;
using Inventory.Application.Repositories;
using Platform.Core.Results;

namespace Inventory.Application.Commands;

// ============================================================
// AdjustStockCommand
// ============================================================

/// <summary>
/// Applies an intentional, auditable correction to a stock item's quantity.
///
/// FLOW:
///   1. Validate stock item exists.
///   2. Create StockAdjustment (records intent + reason).
///   3. Apply the signed delta to InventoryBalance (guards negative result).
///   4. Create StockMovement(Adjustment) with positive magnitude.
///   5. Persist atomically.
/// </summary>
public sealed record AdjustStockCommand(
    Guid StockItemId,
    decimal AdjustmentQuantity,
    AdjustmentReason Reason,
    string? Notes = null);

public sealed class AdjustStockCommandHandler(
    IStockItemRepository stockItemRepository,
    IStockAdjustmentRepository adjustmentRepository,
    IStockMovementRepository movementRepository,
    IInventoryBalanceRepository balanceRepository,
    IInventoryUnitOfWork unitOfWork,
    IAuthorizationService authorization)
{
    public async Task<Result<Guid>> HandleAsync(
        AdjustStockCommand command,
        CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(Inventory.Application.Security.InventoryCapabilities.AdjustStock, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<Guid>(allowed.Error);

        var stockItemId = new StockItemId(command.StockItemId);

        // 1. Validate stock item
        var stockItem = await stockItemRepository.GetByIdAsync(stockItemId, cancellationToken);
        if (stockItem is null)
            return Result.Failure<Guid>(Error.NotFound(
                "Inventory.AdjustStock.StockItemNotFound",
                $"Stock item '{command.StockItemId}' was not found."));

        if (!stockItem.IsActive)
            return Result.Failure<Guid>(Error.Conflict(
                "Inventory.AdjustStock.StockItemInactive",
                "Cannot adjust an inactive stock item."));

        // 2. Create StockAdjustment (domain validates invariants)
        var adjustmentResult = StockAdjustment.Create(
            stockItemId, command.AdjustmentQuantity, command.Reason, command.Notes);
        if (adjustmentResult.IsFailure)
            return Result.Failure<Guid>(adjustmentResult.Error);

        var adjustment = adjustmentResult.Value;
        await adjustmentRepository.AddAsync(adjustment, cancellationToken);

        // 3. Apply delta to InventoryBalance
        var balance = await balanceRepository.GetByStockItemAsync(stockItemId, cancellationToken);
        if (balance is null)
        {
            // No balance yet — create one starting from zero
            var balanceCreateResult = InventoryBalance.Create(stockItemId);
            if (balanceCreateResult.IsFailure)
                return Result.Failure<Guid>(balanceCreateResult.Error);

            balance = balanceCreateResult.Value;
            var applyResult = balance.ApplyAdjustment(command.AdjustmentQuantity);
            if (applyResult.IsFailure) return Result.Failure<Guid>(applyResult.Error);

            await balanceRepository.AddAsync(balance, cancellationToken);
        }
        else
        {
            var applyResult = balance.ApplyAdjustment(command.AdjustmentQuantity);
            if (applyResult.IsFailure) return Result.Failure<Guid>(applyResult.Error);

            balanceRepository.Update(balance);
        }

        // 4. Record the StockMovement fact (always positive magnitude)
        var absQuantity = Quantity.Create(Math.Abs(command.AdjustmentQuantity));
        if (absQuantity.IsFailure) return Result.Failure<Guid>(absQuantity.Error);

        var movementResult = StockMovement.Record(
            stockItemId, MovementType.Adjustment, absQuantity.Value,
            $"Adjustment: {command.Reason}");
        if (movementResult.IsFailure) return Result.Failure<Guid>(movementResult.Error);

        await movementRepository.AddAsync(movementResult.Value, cancellationToken);

        // 5. Persist atomically
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(adjustment.Id.Value);
    }
}
