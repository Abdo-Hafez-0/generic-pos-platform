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
    decimal TaxAmount = 0m);

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
    decimal TaxTotal = 0m);
