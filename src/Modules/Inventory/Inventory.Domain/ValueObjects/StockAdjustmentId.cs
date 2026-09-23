namespace Inventory.Domain.ValueObjects;

/// <summary>Strongly-typed identifier for a StockAdjustment record.</summary>
public readonly record struct StockAdjustmentId(Guid Value)
{
    public static StockAdjustmentId New() => new(Guid.NewGuid());
    public static StockAdjustmentId Empty => new(Guid.Empty);
    public override string ToString() => Value.ToString();
}
