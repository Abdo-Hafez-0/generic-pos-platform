namespace Inventory.Domain.Enums;

/// <summary>
/// Describes why a stock adjustment was made.
///
/// CycleCount    — physical count revealed discrepancy.
/// DamageWrite   — items damaged and removed.
/// Found         — items found that were not previously tracked.
/// TransferIn    — stock received from another location/warehouse.
/// TransferOut   — stock sent to another location/warehouse.
/// OpeningBalance — initial stock entry when starting to track an item.
/// Other         — catch-all with notes required.
/// </summary>
public enum AdjustmentReason
{
    CycleCount = 1,
    DamageWrite = 2,
    Found = 3,
    TransferIn = 4,
    TransferOut = 5,
    OpeningBalance = 6,
    Other = 7
}
