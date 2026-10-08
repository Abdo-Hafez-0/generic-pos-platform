using Platform.Core.Amounts;
using Platform.Core.Results;
using Sales.Domain.ValueObjects;

namespace Sales.Domain.Entities;

/// <summary>
/// Represents a single line item within a Sale.
///
/// HISTORICAL DATA RULE (Architecture §18, Module Map §18):
/// A SaleItem snapshots the price information that existed at the time of the transaction.
/// If the product's price changes later, this SaleItem must NOT change.
/// Therefore:
///   - UnitPrice is the price at time of sale (snapshotted from Catalog/Pricing).
///   - Discount is the discount applied at time of sale.
///   - TaxRate is the tax rate applied at time of sale.
///   - ProductName is captured for display in historical reports.
///
/// PRICES INCLUDE TAX (FIX-08, user decision): UnitPrice and Discount are tax-included amounts; the tax is the part of the line total
/// that is tax (see <see cref="TaxInclusiveLine"/>, the one rule the till uses too). Amounts are rounded per line to 2 decimals.
///
/// SaleItems are owned by a Sale aggregate. They cannot exist independently.
///
/// INVARIANTS:
/// - SaleItemId cannot be empty.
/// - SaleId (parent) cannot be empty.
/// - CatalogProductId cannot be empty.
/// - Quantity must be strictly positive.
/// - UnitPrice cannot be negative.
/// - Discount cannot be negative, and discount amount cannot exceed line total.
/// - TaxRate must be between 0 and 1 (0% to 100%).
///
/// Architecture: Sales.Domain — no EF Core, no WPF, no HTTP.
/// </summary>
public sealed class SaleItem
{
    private SaleItem() { }

    public SaleItemId Id { get; private set; }
    public SaleId SaleId { get; private set; }

    /// <summary>
    /// The Catalog product identifier. Stored as Guid — Sales.Domain must not reference Catalog.Domain.
    /// Validated against Catalog.Contracts at the Application layer.
    /// </summary>
    public Guid CatalogProductId { get; private set; }

    /// <summary>Product name captured at sale time for historical display. Does not change with Catalog.</summary>
    public string ProductName { get; private set; } = string.Empty;

    /// <summary>Product SKU captured at sale time.</summary>
    public string ProductSku { get; private set; } = string.Empty;

    /// <summary>Quantity sold. Must be strictly positive.</summary>
    public SaleQuantity Quantity { get; private set; }

    /// <summary>
    /// Unit price at the time of the sale. Snapshotted — does not change when Catalog price changes.
    /// Architecture §18 Historical Data Rule.
    /// </summary>
    public Money UnitPrice { get; private set; }

    /// <summary>
    /// Discount amount applied to this line item.
    /// Snapshotted at sale time — does not change after the sale.
    /// </summary>
    public Money Discount { get; private set; }

    /// <summary>
    /// Tax rate applied to this line item (e.g., 0.15 = 15%).
    /// Snapshotted at sale time.
    /// </summary>
    public decimal TaxRate { get; private set; }

    /// <summary>The line's amounts under the platform rule (prices include tax, rounded per line).</summary>
    public TaxInclusiveLine Amounts => TaxInclusiveLine.Compute(UnitPrice.Amount, Quantity.Value, Discount.Amount, TaxRate);

    /// <summary>Computed: the line total before tax (LineTotal - TaxAmount).</summary>
    public Money SubTotal => new(Amounts.Net);

    /// <summary>Computed: the tax contained in the line total.</summary>
    public Money TaxAmount => new(Amounts.Tax);

    /// <summary>Computed: (UnitPrice x Quantity) - Discount, tax included - what the customer pays for the line.</summary>
    public Money LineTotal => new(Amounts.Total);

    // -----------------------------------------------------------------------
    // Factory
    // -----------------------------------------------------------------------

    /// <summary>
    /// Creates a SaleItem. Called by Sale.AddItem().
    /// UnitPrice and Discount are snapshotted from the current transaction values.
    /// </summary>
    internal static Result<SaleItem> Create(
        SaleId saleId,
        Guid catalogProductId,
        string productName,
        string productSku,
        SaleQuantity quantity,
        Money unitPrice,
        Money discount,
        decimal taxRate)
    {
        if (saleId == SaleId.Empty)
            return Result.Failure<SaleItem>(Error.Validation(
                "Sales.SaleItem.SaleRequired",
                "SaleItem must be associated with a valid Sale."));

        if (catalogProductId == Guid.Empty)
            return Result.Failure<SaleItem>(Error.Validation(
                "Sales.SaleItem.ProductRequired",
                "A valid Catalog product ID must be provided."));

        if (string.IsNullOrWhiteSpace(productName))
            return Result.Failure<SaleItem>(Error.Validation(
                "Sales.SaleItem.ProductNameRequired",
                "Product name must be provided for historical record."));

        if (string.IsNullOrWhiteSpace(productSku))
            return Result.Failure<SaleItem>(Error.Validation(
                "Sales.SaleItem.ProductSkuRequired",
                "Product SKU must be provided for historical record."));

        if (taxRate < 0m || taxRate > 1m)
            return Result.Failure<SaleItem>(Error.Validation(
                "Sales.SaleItem.TaxRateInvalid",
                "Tax rate must be between 0 and 1 (0% to 100%)."));

        // Discount cannot exceed gross line total
        var grossLineTotal = new Money(TaxInclusiveLine.Round(unitPrice.Amount * quantity.Value));
        if (discount > grossLineTotal)
            return Result.Failure<SaleItem>(Error.Validation(
                "Sales.SaleItem.DiscountExceedsLineTotal",
                "Discount cannot exceed the gross line total (UnitPrice × Quantity)."));

        return Result.Success(new SaleItem
        {
            Id = SaleItemId.New(),
            SaleId = saleId,
            CatalogProductId = catalogProductId,
            ProductName = productName.Trim(),
            ProductSku = productSku.Trim(),
            Quantity = quantity,
            UnitPrice = unitPrice,
            Discount = discount,
            TaxRate = taxRate
        });
    }
}
