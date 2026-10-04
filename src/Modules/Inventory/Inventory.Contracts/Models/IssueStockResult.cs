namespace Inventory.Contracts.Models;

/// <summary>Result of an <see cref="Interfaces.IStockIssueService"/> operation. Never exposes domain types.</summary>
public sealed record IssueStockResult(bool IsSuccess, Guid MovementId, string? ErrorCode, string? ErrorMessage)
{
    public static IssueStockResult Success(Guid movementId) => new(true, movementId, null, null);

    public static IssueStockResult Failure(string errorCode, string errorMessage) =>
        new(false, Guid.Empty, errorCode, errorMessage);
}
