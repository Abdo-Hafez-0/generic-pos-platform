namespace POS.Contracts.Models;

/// <summary>Outcome of a POS operation that returns no data.</summary>
public sealed record POSOperationResult(bool IsSuccess, string? ErrorCode, string? ErrorMessage)
{
    public static POSOperationResult Success() => new(true, null, null);

    public static POSOperationResult Failure(string errorCode, string errorMessage) =>
        new(false, errorCode, errorMessage);
}

/// <summary>Outcome of opening a POS session.</summary>
public sealed record POSOpenSessionResult(bool IsSuccess, Guid SessionId, string? ErrorCode, string? ErrorMessage)
{
    public static POSOpenSessionResult Success(Guid sessionId) => new(true, sessionId, null, null);

    public static POSOpenSessionResult Failure(string errorCode, string errorMessage) =>
        new(false, Guid.Empty, errorCode, errorMessage);
}

/// <summary>Outcome of starting (or resuming) a cart.</summary>
public sealed record POSStartCartResult(bool IsSuccess, Guid CartId, string? ErrorCode, string? ErrorMessage)
{
    public static POSStartCartResult Success(Guid cartId) => new(true, cartId, null, null);

    public static POSStartCartResult Failure(string errorCode, string errorMessage) =>
        new(false, Guid.Empty, errorCode, errorMessage);
}

/// <summary>Outcome of adding a product to a cart. ItemId is the cart line ID.</summary>
public sealed record POSAddItemResult(bool IsSuccess, Guid ItemId, string? ErrorCode, string? ErrorMessage)
{
    public static POSAddItemResult Success(Guid itemId) => new(true, itemId, null, null);

    public static POSAddItemResult Failure(string errorCode, string errorMessage) =>
        new(false, Guid.Empty, errorCode, errorMessage);
}

/// <summary>Outcome of checking out a cart. SaleId is the completed sale in the Sales module.</summary>
public sealed record POSCheckoutResult(bool IsSuccess, Guid SaleId, string? ErrorCode, string? ErrorMessage)
{
    public static POSCheckoutResult Success(Guid saleId) => new(true, saleId, null, null);

    public static POSCheckoutResult Failure(string errorCode, string errorMessage) =>
        new(false, Guid.Empty, errorCode, errorMessage);
}
