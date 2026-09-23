namespace Catalog.Domain.ValueObjects;

/// <summary>Strongly-typed identifier for a Product aggregate.</summary>
public readonly record struct ProductId(Guid Value)
{
    public static ProductId New() => new(Guid.NewGuid());
    public static ProductId Empty => new(Guid.Empty);
    public override string ToString() => Value.ToString();
}
