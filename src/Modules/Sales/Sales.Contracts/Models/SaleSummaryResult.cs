namespace Sales.Contracts.Models;

/// <summary>
/// Immutable DTO representing a Sale summary as seen by other modules through Sales.Contracts.
///
/// This record is the contract boundary — no Sales domain entities cross this line.
/// Other modules (POS, Reporting) consume SaleSummaryResult, never Sale.
///
/// Architecture reference: Module Map §24 (POS consumes Sales.Contracts).
/// </summary>
public sealed record SaleSummaryResult(
    Guid SaleId,
    SaleStatusContract Status,
    string? Reference,
    decimal GrandTotal,
    int ItemCount,
    DateTime CreatedAt,
    DateTime? CompletedAt);
