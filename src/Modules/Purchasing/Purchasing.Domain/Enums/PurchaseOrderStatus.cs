namespace Purchasing.Domain.Enums;

/// <summary>Procurement lifecycle: Draft -> Submitted -> Received; Draft/Submitted -> Cancelled.</summary>
public enum PurchaseOrderStatus
{
    /// <summary>Being edited (lines can change).</summary>
    Draft = 1,

    /// <summary>Sent to the supplier; lines are frozen; waiting for goods.</summary>
    Submitted = 2,

    /// <summary>Every line was received into stock (terminal).</summary>
    Received = 3,

    /// <summary>Cancelled before any stock was received (terminal).</summary>
    Cancelled = 4
}
