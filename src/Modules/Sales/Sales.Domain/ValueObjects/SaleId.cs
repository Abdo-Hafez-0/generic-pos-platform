namespace Sales.Domain.ValueObjects;

/// <summary>Strongly-typed identifier for a Sale aggregate.</summary>
public readonly record struct SaleId(Guid Value)
{
    public static SaleId New() => new(Guid.NewGuid());
    public static SaleId Empty => new(Guid.Empty);
    public override string ToString() => Value.ToString();
}
