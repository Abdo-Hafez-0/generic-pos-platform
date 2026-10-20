namespace Catalog.Contracts.Models;

/// <summary>MISS-11b: a product as another module's per-product list needs it: its SKU, name, unit and barcodes.</summary>
public sealed record CatalogProductListItem(Guid ProductId, string Sku, string Name, Guid UnitId, string UnitName, string UnitAbbreviation,
    IReadOnlyList<string> Barcodes, bool IsActive);

/// <summary>MISS-11b: a unit of measure.</summary>
public sealed record CatalogUnitListItem(Guid UnitId, string Name, string Abbreviation, bool IsActive);
