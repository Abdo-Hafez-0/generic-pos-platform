namespace Inventory.Domain.ValueObjects;

/// <summary>Strongly-typed identifier for a Warehouse aggregate.</summary>
public readonly record struct WarehouseId(Guid Value)
{
    public static WarehouseId New() => new(Guid.NewGuid());
    public static WarehouseId Empty => new(Guid.Empty);
    public override string ToString() => Value.ToString();
}
