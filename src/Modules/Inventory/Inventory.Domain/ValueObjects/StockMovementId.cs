namespace Inventory.Domain.ValueObjects;

/// <summary>Strongly-typed identifier for a StockMovement record.</summary>
public readonly record struct StockMovementId(Guid Value)
{
    public static StockMovementId New() => new(Guid.NewGuid());
    public static StockMovementId Empty => new(Guid.Empty);
    public override string ToString() => Value.ToString();
}
