namespace Purchasing.Domain.Enums;

/// <summary>
/// Procurement lifecycle: Draft -> Submitted -> (PartiallyReceived ->) Received; Draft/Submitted -> Cancelled; PartiallyReceived -> Closed
/// (closed short: the rest will not come). The numbers are stored; never renumber them.
/// </summary>
public enum PurchaseOrderStatus
{
    /// <summary>Being edited (lines can change).</summary>
    Draft = 1,

    /// <summary>Sent to the supplier; lines are frozen; waiting for goods.</summary>
    Submitted = 2,

    /// <summary>Every ordered unit was received into stock (terminal).</summary>
    Received = 3,

    /// <summary>Cancelled before any stock was received (terminal).</summary>
    Cancelled = 4,

    /// <summary>Some goods were received; the rest is still expected (FIX-09).</summary>
    PartiallyReceived = 5,

    /// <summary>Closed short: part was received and the rest will not come (terminal, FIX-09).</summary>
    Closed = 6
}
