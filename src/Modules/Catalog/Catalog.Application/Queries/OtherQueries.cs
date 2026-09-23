using Catalog.Application.DTOs;
using Catalog.Application.Mapping;
using Catalog.Application.Repositories;
using Catalog.Contracts.Models;
using Platform.Core.Results;

namespace Catalog.Application.Queries;

// ============================================================
// FindProductByBarcodeQuery
// ============================================================

public sealed record FindProductByBarcodeQuery(string BarcodeValue);

public sealed class FindProductByBarcodeQueryHandler(
    IBarcodeRepository barcodeRepository,
    ICategoryRepository categoryRepository,
    IUnitRepository unitRepository)
{
    public async Task<Result<ProductLookupResult>> HandleAsync(
        FindProductByBarcodeQuery query,
        CancellationToken cancellationToken = default)
    {
        var product = await barcodeRepository.GetProductByBarcodeValueAsync(query.BarcodeValue, cancellationToken);
        if (product is null)
            return Result.Failure<ProductLookupResult>(
                Error.NotFound("Catalog.Barcode.NotFound", $"No product found for barcode '{query.BarcodeValue}'."));

        var category = await categoryRepository.GetByIdAsync(product.CategoryId, cancellationToken);
        var unit = await unitRepository.GetByIdAsync(product.UnitId, cancellationToken);

        var result = new ProductLookupResult(
            product.Id.Value,
            product.Sku,
            product.Name,
            product.Description,
            product.CategoryId.Value,
            category?.Name ?? "Unknown",
            product.UnitId.Value,
            unit?.Name ?? "Unknown",
            unit?.Abbreviation ?? "",
            product.SalePrice,
            product.CostPrice,
            (ProductStatusContract)(int)product.Status);

        return Result.Success(result);
    }
}

// ============================================================
// GetAllCategoriesQuery
// ============================================================

public sealed record GetAllCategoriesQuery;

public sealed class GetAllCategoriesQueryHandler(ICategoryRepository categoryRepository)
{
    public async Task<Result<IReadOnlyList<CategoryDto>>> HandleAsync(
        GetAllCategoriesQuery query,
        CancellationToken cancellationToken = default)
    {
        var categories = await categoryRepository.GetAllActiveAsync(cancellationToken);
        var dtos = categories.Select(DtoMapper.ToDto).ToList();
        return Result.Success<IReadOnlyList<CategoryDto>>(dtos);
    }
}

// ============================================================
// GetAllUnitsQuery
// ============================================================

public sealed record GetAllUnitsQuery;

public sealed class GetAllUnitsQueryHandler(IUnitRepository unitRepository)
{
    public async Task<Result<IReadOnlyList<UnitDto>>> HandleAsync(
        GetAllUnitsQuery query,
        CancellationToken cancellationToken = default)
    {
        var units = await unitRepository.GetAllActiveAsync(cancellationToken);
        var dtos = units.Select(DtoMapper.ToDto).ToList();
        return Result.Success<IReadOnlyList<UnitDto>>(dtos);
    }
}
