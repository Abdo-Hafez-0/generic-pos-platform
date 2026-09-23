using Catalog.Application.DTOs;
using Catalog.Domain.Entities;

namespace Catalog.Application.Mapping;

/// <summary>
/// Maps domain entities to application DTOs.
/// Lives in Application layer — no EF Core, no domain entity exposure to outside layers.
/// </summary>
internal static class DtoMapper
{
    public static ProductDto ToDto(
        Product product,
        string categoryName,
        string unitName,
        string unitAbbreviation)
    {
        return new ProductDto(
            product.Id.Value,
            product.Sku,
            product.Name,
            product.Description,
            product.CategoryId.Value,
            categoryName,
            product.UnitId.Value,
            unitName,
            unitAbbreviation,
            product.Status,
            product.SalePrice,
            product.CostPrice,
            product.CreatedAt,
            product.UpdatedAt,
            product.Barcodes.Select(b => new BarcodeDto(b.Id.Value, b.Value, b.Format)).ToList());
    }

    public static CategoryDto ToDto(Category category) =>
        new(category.Id.Value, category.Name, category.Description, category.IsActive, category.CreatedAt);

    public static UnitDto ToDto(Unit unit) =>
        new(unit.Id.Value, unit.Name, unit.Abbreviation, unit.IsActive);
}
