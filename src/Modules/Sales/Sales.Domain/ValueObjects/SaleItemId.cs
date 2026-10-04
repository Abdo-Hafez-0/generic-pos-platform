namespace Sales.Domain.ValueObjects;

/// <summary>Strongly-typed identifier for a SaleItem entity.</summary>
public readonly record struct SaleItemId(Guid Value)
{
    public static SaleItemId New() => new(Guid.NewGuid());
    public static SaleItemId Empty => new(Guid.Empty);
    public override string ToString() => Value.ToString();
}
