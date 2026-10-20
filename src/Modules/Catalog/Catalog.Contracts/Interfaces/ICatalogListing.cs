using Catalog.Contracts.Models;

namespace Catalog.Contracts.Interfaces;

/// <summary>
/// MISS-11b: the whole catalogue for another module that keeps its own data PER product or unit (Fiscal: the tax authority's codes). Read
/// only; never exposes Catalog.Domain types. Implemented by Catalog.Infrastructure.
/// </summary>
public interface ICatalogListing
{
    /// <summary>Every product (only the active ones unless <paramref name="activeOnly"/> is false) with its unit and barcodes, by name.</summary>
    Task<IReadOnlyList<CatalogProductListItem>> ListProductsAsync(bool activeOnly = true, CancellationToken cancellationToken = default);

    /// <summary>Every unit of measure, by name.</summary>
    Task<IReadOnlyList<CatalogUnitListItem>> ListUnitsAsync(CancellationToken cancellationToken = default);
}
