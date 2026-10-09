using POS.Contracts.Models;
using POS.Domain.Entities;
using POS.Domain.Enums;

namespace POS.Application.Mapping;

/// <summary>Maps POS domain entities to the read models exposed through POS.Contracts.</summary>
public static class PosMapping
{
    public static POSSessionResult ToResult(this PosSession session) => new(
        SessionId: session.Id.Value,
        CashierReference: session.CashierReference,
        WarehouseId: session.WarehouseId,
        Status: session.Status switch
        {
            PosSessionStatus.Open => POSSessionStatusContract.Open,
            _ => POSSessionStatusContract.Closed
        },
        OpenedAt: session.OpenedAt,
        ClosedAt: session.ClosedAt);

    public static POSCartResult ToResult(this PosCart cart) => new(
        CartId: cart.Id.Value,
        SessionId: cart.SessionId.Value,
        Status: cart.Status switch
        {
            PosCartStatus.Open => POSCartStatusContract.Open,
            _ => POSCartStatusContract.CheckedOut
        },
        // the charged amounts: each line with its own discount and its share of a cart discount (FIX-08c)
        Items: cart.PricedLines.Select(l => new POSCartItemResult(
            ItemId: l.Item.Id.Value,
            ProductId: l.Item.CatalogProductId,
            ProductSku: l.Item.ProductSku,
            ProductName: l.Item.ProductName,
            Quantity: l.Item.Quantity.Value,
            UnitPrice: l.Item.UnitPrice.Amount,
            LineTotal: l.Amounts.Total,
            TaxRate: l.Item.TaxRate,
            TaxAmount: l.Amounts.Tax,
            Discount: l.Amounts.Discount,
            LineDiscountKind: l.Item.LineDiscount is { } rule ? (POSDiscountKind)(int)rule.Kind : null,
            LineDiscountValue: l.Item.LineDiscount?.Value)).ToList().AsReadOnly(),
        Subtotal: cart.Subtotal.Amount,
        Total: cart.Total.Amount,
        SaleId: cart.SaleId,
        CreatedAt: cart.CreatedAt,
        CheckedOutAt: cart.CheckedOutAt,
        TaxTotal: cart.TaxTotal.Amount,
        DiscountTotal: cart.DiscountTotal.Amount,
        CartDiscountKind: cart.CartDiscount is { } given ? (POSDiscountKind)(int)given.Kind : null,
        CartDiscountValue: cart.CartDiscount?.Value,
        CustomerId: cart.CustomerId,
        CustomerCode: cart.CustomerCode,
        CustomerName: cart.CustomerName);
}
