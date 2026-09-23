using Platform.Core.Results;
using Catalog.Domain.ValueObjects;
using Catalog.Domain.Enums;

namespace Catalog.Domain.Entities;

/// <summary>
/// Represents a barcode assigned to a product.
/// A product may have multiple barcodes (e.g., EAN-13 and QR code).
/// Barcodes are owned by the Product aggregate — never accessed independently by other modules.
/// Other modules resolve barcodes through Catalog.Contracts.IProductBarcodeResolver.
/// </summary>
public sealed class Barcode
{
    private Barcode() { }

    public BarcodeId Id { get; private set; }
    public ProductId ProductId { get; private set; }
    public string Value { get; private set; } = string.Empty;
    public BarcodeFormat Format { get; private set; }
    public DateTime CreatedAt { get; private set; }

    /// <summary>Creates a new Barcode for a given product.</summary>
    internal static Result<Barcode> Create(ProductId productId, string value, BarcodeFormat format)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Result.Failure<Barcode>(
                Error.Validation("Catalog.Barcode.ValueEmpty", "Barcode value cannot be empty."));

        if (value.Length > 100)
            return Result.Failure<Barcode>(
                Error.Validation("Catalog.Barcode.ValueTooLong", "Barcode value cannot exceed 100 characters."));

        return Result.Success(new Barcode
        {
            Id = BarcodeId.New(),
            ProductId = productId,
            Value = value.Trim(),
            Format = format,
            CreatedAt = DateTime.UtcNow
        });
    }
}
