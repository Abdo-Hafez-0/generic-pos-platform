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
        Items: cart.Items.Select(i => new POSCartItemResult(
            ItemId: i.Id.Value,
            ProductId: i.CatalogProductId,
            ProductSku: i.ProductSku,
            ProductName: i.ProductName,
            Quantity: i.Quantity.Value,
            UnitPrice: i.UnitPrice.Amount,
            LineTotal: i.LineTotal.Amount)).ToList().AsReadOnly(),
        Subtotal: cart.Subtotal.Amount,
        Total: cart.Total.Amount,
        SaleId: cart.SaleId,
        CreatedAt: cart.CreatedAt,
        CheckedOutAt: cart.CheckedOutAt);
}
