using Platform.Core.Results;
using POS.Domain.ValueObjects;

namespace POS.Domain.Entities;

/// <summary>
/// A product line in a POS cart.
///
/// HISTORICAL DATA RULE: ProductName, ProductSku and UnitPrice are snapshots taken when the
/// product is added. They never change when the Catalog changes. CatalogProductId is a plain
/// Guid — POS holds no reference to Catalog domain types.
/// </summary>
public sealed class PosCartItem
{
    private PosCartItem() { }

    public PosCartItemId Id { get; private set; }
    public PosCartId CartId { get; private set; }

    public Guid CatalogProductId { get; private set; }
    public string ProductSku { get; private set; } = string.Empty;
    public string ProductName { get; private set; } = string.Empty;

    public CartQuantity Quantity { get; private set; }

    /// <summary>Unit price snapshot at the time the item was added.</summary>
    public Money UnitPrice { get; private set; }

    public Money LineTotal => UnitPrice * Quantity.Value;

    internal static Result<PosCartItem> Create(
        PosCartId cartId,
        Guid catalogProductId,
        string productSku,
        string productName,
        CartQuantity quantity,
        Money unitPrice)
    {
        if (cartId == PosCartId.Empty)
            return Result.Failure<PosCartItem>(Error.Validation(
                "POS.CartItem.CartRequired", "A cart item must belong to a cart."));

        if (catalogProductId == Guid.Empty)
            return Result.Failure<PosCartItem>(Error.Validation(
                "POS.CartItem.ProductRequired", "A valid Catalog product ID must be provided."));

        if (string.IsNullOrWhiteSpace(productSku))
            return Result.Failure<PosCartItem>(Error.Validation(
                "POS.CartItem.SkuRequired", "Product SKU must be provided."));

        if (string.IsNullOrWhiteSpace(productName))
            return Result.Failure<PosCartItem>(Error.Validation(
                "POS.CartItem.NameRequired", "Product name must be provided."));

        return Result.Success(new PosCartItem
        {
            Id = PosCartItemId.New(),
            CartId = cartId,
            CatalogProductId = catalogProductId,
            ProductSku = productSku.Trim(),
            ProductName = productName.Trim(),
            Quantity = quantity,
            UnitPrice = unitPrice
        });
    }

    internal void SetQuantity(CartQuantity quantity) => Quantity = quantity;
}
