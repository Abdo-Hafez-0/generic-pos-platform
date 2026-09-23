namespace Inventory.Domain.ValueObjects;

/// <summary>Strongly-typed identifier for a Location aggregate (bin/shelf/zone inside a warehouse).</summary>
public readonly record struct LocationId(Guid Value)
{
    public static LocationId New() => new(Guid.NewGuid());
    public static LocationId Empty => new(Guid.Empty);
    public override string ToString() => Value.ToString();
}
