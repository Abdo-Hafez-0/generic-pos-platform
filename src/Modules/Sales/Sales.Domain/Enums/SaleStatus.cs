namespace Sales.Domain.Enums;

/// <summary>
/// Defines the lifecycle status of a Sale.
///
/// Valid transitions:
///   Draft -> Confirmed -> Completed
///   Draft -> Cancelled
///   Confirmed -> Cancelled
///
/// CompletedSales and Cancelled sales are terminal — no further mutation allowed.
///
/// Architecture reference: Module Map §17 (Sales Owns).
/// </summary>
public enum SaleStatus
{
    /// <summary>Sale is being created/built. Items may be added or removed.</summary>
    Draft = 1,

    /// <summary>Sale has been confirmed. Items are finalised; awaiting completion.</summary>
    Confirmed = 2,

    /// <summary>Sale is fully completed. Historical data is frozen.</summary>
    Completed = 3,

    /// <summary>Sale was cancelled before completion. Terminal state.</summary>
    Cancelled = 4
}
