namespace POS.Contracts.Models;

public enum POSSessionStatusContract
{
    Open = 1,
    Closed = 2
}

public enum POSCartStatusContract
{
    Open = 1,
    CheckedOut = 2
}

/// <summary>Read model of a POS session. Never exposes POS.Domain types.</summary>
public sealed record POSSessionResult(
    Guid SessionId,
    string CashierReference,
    Guid WarehouseId,
    POSSessionStatusContract Status,
    DateTime OpenedAt,
    DateTime? ClosedAt);

/// <summary>Read model of one cart line (prices are snapshots taken when the line was added).</summary>
/// <summary>A warehouse a till session can sell from.</summary>
public sealed record POSWarehouseResult(Guid WarehouseId, string Code, string Name);

public sealed record POSCartItemResult(
    Guid ItemId,
    Guid ProductId,
    string ProductSku,
    string ProductName,
    decimal Quantity,
    decimal UnitPrice,
    decimal LineTotal,
    decimal TaxRate = 0m,
    decimal TaxAmount = 0m,
    decimal Discount = 0m,
    POSDiscountKind? LineDiscountKind = null,
    decimal? LineDiscountValue = null);

/// <summary>How a discount was given (FIX-08c).</summary>
public enum POSDiscountKind
{
    /// <summary>A percentage (10 = 10%).</summary>
    Percent = 1,

    /// <summary>A fixed amount off, tax included.</summary>
    Amount = 2
}

/// <summary>Read model of a cart with totals.</summary>
public sealed record POSCartResult(
    Guid CartId,
    Guid SessionId,
    POSCartStatusContract Status,
    IReadOnlyList<POSCartItemResult> Items,
    decimal Subtotal,
    decimal Total,
    Guid? SaleId,
    DateTime CreatedAt,
    DateTime? CheckedOutAt,
    decimal TaxTotal = 0m,
    decimal DiscountTotal = 0m,
    POSDiscountKind? CartDiscountKind = null,
    decimal? CartDiscountValue = null,
    Guid? CustomerId = null,
    string? CustomerCode = null,
    string? CustomerName = null);

/// <summary>A customer as the till may show them (FIX-11): code and name only, never contact details.</summary>
public sealed record POSCustomerResult(Guid CustomerId, string Code, string Name);

/// <summary>Customers found for the till; a failure when the Customers module is not installed or the user may not sell.</summary>
public sealed record POSCustomerSearchResult(bool IsSuccess, IReadOnlyList<POSCustomerResult> Customers, string? ErrorCode = null, string? ErrorMessage = null)
{
    public static POSCustomerSearchResult Success(IReadOnlyList<POSCustomerResult> customers) => new(true, customers);

    public static POSCustomerSearchResult Failure(string errorCode, string errorMessage) => new(false, [], errorCode, errorMessage);
}
