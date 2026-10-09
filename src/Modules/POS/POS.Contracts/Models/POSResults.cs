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
/// HardwareNotices lists peripheral problems (receipt not printed, drawer not opened) that occurred AFTER the sale was completed and
/// saved: the sale is valid regardless, the notices only tell the cashier what to do by hand.
/// </summary>
/// FIX-10: with several payments, PaymentIds lists them all (PaymentId is the first) and ChangeDue is the change from the cash.
public sealed record POSCheckoutResult(
    bool IsSuccess, Guid SaleId, string? ErrorCode, string? ErrorMessage, Guid? PaymentId = null, decimal ChangeDue = 0m,
    IReadOnlyList<POSHardwareNotice>? HardwareNotices = null, IReadOnlyList<Guid>? PaymentIds = null)
{
    public static POSCheckoutResult Success(Guid saleId, Guid? paymentId = null, decimal changeDue = 0m, IReadOnlyList<POSHardwareNotice>? hardwareNotices = null,
        IReadOnlyList<Guid>? paymentIds = null) =>
        new(true, saleId, null, null, paymentId, changeDue, hardwareNotices, paymentIds);

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

/// <summary>A payment to record at checkout: the whole cart total, or one part of a split payment (FIX-10).</summary>
/// <param name="Method">Payment method.</param>
/// <param name="TenderedAmount">Cash handed over (cash only); must cover <paramref name="Amount"/>. Only cash gives change; it is returned in the result.</param>
/// <param name="MethodDetail">Required for <see cref="POSPaymentMethod.Other"/>; an optional note otherwise (e.g. a card approval code).</param>
/// <param name="Amount">What this payment pays of the sale; null = the whole cart total (a single payment). In a split payment every part names its amount and the amounts add up to the total.</param>
public sealed record POSPaymentRequest(POSPaymentMethod Method, decimal? TenderedAmount = null, string? MethodDetail = null, decimal? Amount = null);
