namespace Inventory.Domain.ValueObjects;

/// <summary>
/// Strongly-typed identifier for a StockItem aggregate.
/// Inventory-owned — does not reference Catalog.Domain.ValueObjects.ProductId.
/// </summary>
public readonly record struct StockItemId(Guid Value)
{
    public static StockItemId New() => new(Guid.NewGuid());
    public static StockItemId Empty => new(Guid.Empty);
    public override string ToString() => Value.ToString();
}
