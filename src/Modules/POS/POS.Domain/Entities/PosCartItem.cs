using Platform.Core.Amounts;
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

    /// <summary>Unit price snapshot at the time the item was added (tax included - FIX-08).</summary>
    public Money UnitPrice { get; private set; }

    /// <summary>Tax rate snapshot at the time the item was added (0.14 = 14%; 0 = no tax). FIX-08b.</summary>
    public decimal TaxRate { get; private set; }

    /// <summary>Stored form of <see cref="LineDiscount"/> (FIX-08c): null = no line discount.</summary>
    public DiscountKind? LineDiscountKind { get; private set; }

    public decimal? LineDiscountValue { get; private set; }

    /// <summary>The discount the cashier gave on this line, as given (10%, or 2.00 off), or null. FIX-08c.</summary>
    public DiscountRule? LineDiscount => LineDiscountKind is { } kind && LineDiscountValue is { } value ? new DiscountRule(kind, value) : null;

    /// <summary>Unit price x quantity, rounded - what the line costs before any discount.</summary>
    public decimal Gross => TaxInclusiveLine.Round(UnitPrice.Amount * Quantity.Value);

    /// <summary>The line discount as an amount against the current quantity (never more than the line).</summary>
    public decimal LineDiscountAmount => LineDiscount?.AmountOf(Gross) ?? 0m;

    /// <summary>
    /// The line's amounts with ITS OWN discount only (prices include tax, rounded per line). The amounts that are charged - including the
    /// line's share of a cart discount - come from <see cref="PosCart.PricedLines"/>.
    /// </summary>
    public TaxInclusiveLine Amounts => TaxInclusiveLine.Compute(UnitPrice.Amount, Quantity.Value, LineDiscountAmount, TaxRate);

    /// <summary>What the line costs after its own discount, tax included (before a cart discount).</summary>
    public Money LineTotal => new(Amounts.Total);

    /// <summary>The tax contained in <see cref="LineTotal"/>.</summary>
    public Money TaxAmount => new(Amounts.Tax);

    internal static Result<PosCartItem> Create(
        PosCartId cartId,
        Guid catalogProductId,
        string productSku,
        string productName,
        CartQuantity quantity,
        Money unitPrice,
        decimal taxRate = 0m)
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

        if (taxRate < 0m || taxRate > 1m)
            return Result.Failure<PosCartItem>(Error.Validation(
                "POS.CartItem.TaxRateInvalid", "The tax rate must be between 0% and 100%."));

        return Result.Success(new PosCartItem
        {
            Id = PosCartItemId.New(),
            CartId = cartId,
            CatalogProductId = catalogProductId,
            ProductSku = productSku.Trim(),
            ProductName = productName.Trim(),
            Quantity = quantity,
            UnitPrice = unitPrice,
            TaxRate = taxRate
        });
    }

    internal void SetQuantity(CartQuantity quantity) => Quantity = quantity;

    internal void SetLineDiscount(DiscountRule? rule)
    {
        LineDiscountKind = rule?.Kind;
        LineDiscountValue = rule?.Value;
    }
}
