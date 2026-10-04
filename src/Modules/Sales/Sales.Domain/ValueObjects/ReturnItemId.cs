namespace Sales.Domain.ValueObjects;

/// <summary>Strongly-typed identifier for a ReturnItem entity.</summary>
public readonly record struct ReturnItemId(Guid Value)
{
    public static ReturnItemId New() => new(Guid.NewGuid());
    public static ReturnItemId Empty => new(Guid.Empty);
    public override string ToString() => Value.ToString();
}
