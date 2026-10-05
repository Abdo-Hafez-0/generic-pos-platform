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

/// <summary>
/// Outcome of checking out a cart. SaleId is the completed sale in the Sales module. When a payment was requested
/// (and the Payments module is installed), PaymentId is the recorded payment and ChangeDue the cash change to give back.
/// </summary>
public sealed record POSCheckoutResult(bool IsSuccess, Guid SaleId, string? ErrorCode, string? ErrorMessage, Guid? PaymentId = null, decimal ChangeDue = 0m)
{
    public static POSCheckoutResult Success(Guid saleId, Guid? paymentId = null, decimal changeDue = 0m) =>
        new(true, saleId, null, null, paymentId, changeDue);

    public static POSCheckoutResult Failure(string errorCode, string errorMessage) =>
        new(false, Guid.Empty, errorCode, errorMessage);
}

/// <summary>How the customer pays at checkout (recorded through the optional Payments module; nothing is processed externally).</summary>
public enum POSPaymentMethod
{
    Cash = 1,
    Card = 2,
    Other = 3
}

/// <summary>An optional payment to record at checkout for the full cart total.</summary>
/// <param name="Method">Payment method.</param>
/// <param name="TenderedAmount">Cash handed over (cash only); must cover the total. The change is returned in the result.</param>
/// <param name="MethodDetail">Required for <see cref="POSPaymentMethod.Other"/>; optional description otherwise.</param>
public sealed record POSPaymentRequest(POSPaymentMethod Method, decimal? TenderedAmount = null, string? MethodDetail = null);
