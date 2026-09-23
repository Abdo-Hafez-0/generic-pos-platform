using Catalog.Domain.Enums;

namespace Catalog.Application.DTOs;

/// <summary>Application-level DTO for a product. Used by UI and query handlers.</summary>
public sealed record ProductDto(
    Guid Id,
    string Sku,
    string Name,
    string? Description,
    Guid CategoryId,
    string CategoryName,
    Guid UnitId,
    string UnitName,
    string UnitAbbreviation,
    ProductStatus Status,
    decimal SalePrice,
    decimal? CostPrice,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<BarcodeDto> Barcodes);
