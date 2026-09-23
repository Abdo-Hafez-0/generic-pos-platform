namespace Inventory.Domain.Enums;

/// <summary>
/// Classifies the direction and purpose of a stock movement.
///
/// StockIn  — stock received (purchase, opening balance, found stock).
/// StockOut — stock removed (sold, consumed, written off).
/// Adjustment — intentional correction driven by StockAdjustment.
/// Transfer — stock moved between locations within the same warehouse,
///            or between warehouses (future: two movements, one per side).
/// </summary>
public enum MovementType
{
    StockIn = 1,
    StockOut = 2,
    Adjustment = 3,
    Transfer = 4
}
