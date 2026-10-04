namespace Sales.Contracts.Models;

/// <summary>
/// Contract-level representation of sale status.
/// Mirrors Sales.Domain.Enums.SaleStatus but is completely independent of domain.
/// Other modules (POS, Reporting) use this enum without referencing Sales.Domain.
/// </summary>
public enum SaleStatusContract
{
    Draft = 1,
    Confirmed = 2,
    Completed = 3,
    Cancelled = 4
}
