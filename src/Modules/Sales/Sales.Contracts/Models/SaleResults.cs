namespace Sales.Contracts.Models;

/// <summary>
/// Result of a CreateSale operation exposed through ISalesService.
/// Carries success/failure information without exposing Sales.Domain entities.
/// </summary>
public sealed record CreateSaleResult(bool IsSuccess, Guid SaleId, string? ErrorCode, string? ErrorMessage)
{
    public static CreateSaleResult Success(Guid saleId) =>
        new(true, saleId, null, null);

    public static CreateSaleResult Failure(string errorCode, string errorMessage) =>
        new(false, Guid.Empty, errorCode, errorMessage);
}

/// <summary>
/// Result of an AddSaleItem operation exposed through ISalesService.
/// </summary>
public sealed record AddSaleItemResult(bool IsSuccess, Guid SaleItemId, string? ErrorCode, string? ErrorMessage)
{
    public static AddSaleItemResult Success(Guid saleItemId) =>
        new(true, saleItemId, null, null);

    public static AddSaleItemResult Failure(string errorCode, string errorMessage) =>
        new(false, Guid.Empty, errorCode, errorMessage);
}

/// <summary>
/// General operation result for Sale state transitions (Confirm, Complete, Cancel).
/// </summary>
public sealed record SaleOperationResult(bool IsSuccess, string? ErrorCode, string? ErrorMessage)
{
    public static SaleOperationResult Success() =>
        new(true, null, null);

    public static SaleOperationResult Failure(string errorCode, string errorMessage) =>
        new(false, errorCode, errorMessage);
}
