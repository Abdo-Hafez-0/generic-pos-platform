namespace Catalog.Domain.ValueObjects;

/// <summary>Strongly-typed identifier for a Unit entity.</summary>
public readonly record struct UnitId(Guid Value)
{
    public static UnitId New() => new(Guid.NewGuid());
    public static UnitId Empty => new(Guid.Empty);
    public override string ToString() => Value.ToString();
}
