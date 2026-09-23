namespace Catalog.Domain.ValueObjects;

/// <summary>Strongly-typed identifier for a Barcode entity.</summary>
public readonly record struct BarcodeId(Guid Value)
{
    public static BarcodeId New() => new(Guid.NewGuid());
    public static BarcodeId Empty => new(Guid.Empty);
    public override string ToString() => Value.ToString();
}
