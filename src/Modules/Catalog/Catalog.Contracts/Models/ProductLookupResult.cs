namespace Catalog.Contracts.Models;

/// <summary>
/// Immutable DTO representing a product as seen by other modules through Catalog.Contracts.
///
/// This record is the contract boundary — no Catalog domain entities cross this line.
/// Other modules (Inventory, Sales, POS) consume ProductLookupResult, never Product.
///
/// Architecture reference: Module Map §10 (Catalog Contracts).
/// </summary>
public sealed record ProductLookupResult(
    Guid ProductId,
    string Sku,
    string Name,
    string? Description,
    Guid CategoryId,
    string CategoryName,
    Guid UnitId,
    string UnitName,
    string UnitAbbreviation,
    decimal SalePrice,
    decimal? CostPrice,
    ProductStatusContract Status);
