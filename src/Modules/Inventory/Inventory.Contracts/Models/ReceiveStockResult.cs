namespace Inventory.Contracts.Models;

/// <summary>Result of an <see cref="Interfaces.IStockReceiptService"/> operation. Never exposes domain types.</summary>
public sealed record ReceiveStockResult(bool IsSuccess, Guid MovementId, string? ErrorCode, string? ErrorMessage)
{
    public static ReceiveStockResult Success(Guid movementId) => new(true, movementId, null, null);

    public static ReceiveStockResult Failure(string errorCode, string errorMessage) =>
        new(false, Guid.Empty, errorCode, errorMessage);
}
