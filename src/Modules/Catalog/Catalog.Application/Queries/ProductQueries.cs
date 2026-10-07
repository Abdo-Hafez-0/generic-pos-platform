using Catalog.Application.DTOs;
using Catalog.Application.Mapping;
using Catalog.Application.Repositories;
using Catalog.Domain.ValueObjects;
using Catalog.Contracts.Interfaces;
using Catalog.Contracts.Models;
using Platform.Application.Abstractions.Authorization;
using Platform.Core.Results;

namespace Catalog.Application.Queries;

// ============================================================
// GetProductByIdQuery
// ============================================================

public sealed record GetProductByIdQuery(Guid ProductId);

public sealed class GetProductByIdQueryHandler(
    IProductRepository productRepository,
    ICategoryRepository categoryRepository,
    IUnitRepository unitRepository,
    IAuthorizationService authorization)
{
    public async Task<Result<ProductDto>> HandleAsync(
        GetProductByIdQuery query,
        CancellationToken cancellationToken = default)
    {
        var product = await productRepository.GetByIdAsync(new ProductId(query.ProductId), cancellationToken);
        if (product is null)
            return Result.Failure<ProductDto>(
                Error.NotFound("Catalog.Product.NotFound", $"Product '{query.ProductId}' was not found."));

        var category = await categoryRepository.GetByIdAsync(product.CategoryId, cancellationToken);
        var unit = await unitRepository.GetByIdAsync(product.UnitId, cancellationToken);

        var dto = DtoMapper.ToDto(
            product,
            category?.Name ?? "Unknown",
            unit?.Name ?? "Unknown",
            unit?.Abbreviation ?? "");

        return Result.Success(await CostVisibility.ApplyAsync(dto, authorization, cancellationToken));
    }
}

// ============================================================
// GetProductBySkuQuery
// ============================================================

public sealed record GetProductBySkuQuery(string Sku);

public sealed class GetProductBySkuQueryHandler(
    IProductRepository productRepository,
    ICategoryRepository categoryRepository,
    IUnitRepository unitRepository,
    IAuthorizationService authorization)
{
    public async Task<Result<ProductDto>> HandleAsync(
        GetProductBySkuQuery query,
        CancellationToken cancellationToken = default)
    {
        var product = await productRepository.GetBySkuAsync(query.Sku, cancellationToken);
        if (product is null)
            return Result.Failure<ProductDto>(
                Error.NotFound("Catalog.Product.NotFound", $"Product with SKU '{query.Sku}' was not found."));

        var category = await categoryRepository.GetByIdAsync(product.CategoryId, cancellationToken);
        var unit = await unitRepository.GetByIdAsync(product.UnitId, cancellationToken);

        var dto = DtoMapper.ToDto(
            product,
            category?.Name ?? "Unknown",
            unit?.Name ?? "Unknown",
            unit?.Abbreviation ?? "");

        return Result.Success(await CostVisibility.ApplyAsync(dto, authorization, cancellationToken));
    }
}

// ============================================================
// SearchProductsQuery (FIX-01c: the products screen)
// ============================================================

/// <summary>
/// Products for the products screen: name or SKU containing the search text, or exactly that barcode; active products unless
/// <see cref="IncludeInactive"/>. At most <see cref="MaxResults"/> rows, ordered by name; <see cref="ProductSearchResult.IsTruncated"/>
/// tells the screen to ask for a narrower search. Cost prices follow the same visibility rule as every other product read.
/// </summary>
public sealed record SearchProductsQuery(string? Search, bool IncludeInactive = false)
{
    public const int MaxResults = 500;
}

public sealed record ProductSearchResult(IReadOnlyList<ProductDto> Items, bool IsTruncated);

public sealed class SearchProductsQueryHandler(
    IProductRepository productRepository,
    ICategoryRepository categoryRepository,
    IUnitRepository unitRepository,
    IAuthorizationService authorization)
{
    public async Task<Result<ProductSearchResult>> HandleAsync(
        SearchProductsQuery query,
        CancellationToken cancellationToken = default)
    {
        var products = await productRepository.SearchAsync(query.Search, query.IncludeInactive, SearchProductsQuery.MaxResults + 1, cancellationToken);
        var truncated = products.Count > SearchProductsQuery.MaxResults;

        var categories = new Dictionary<CategoryId, (string Name, bool Found)>();
        var units = new Dictionary<UnitId, (string Name, string Abbreviation)>();
        var showCost = await authorization.IsAllowedAsync(Catalog.Application.Security.CatalogCapabilities.ViewCost, cancellationToken);

        var items = new List<ProductDto>(Math.Min(products.Count, SearchProductsQuery.MaxResults));
        foreach (var product in products.Take(SearchProductsQuery.MaxResults))
        {
            if (!categories.TryGetValue(product.CategoryId, out var category))
            {
                var found = await categoryRepository.GetByIdAsync(product.CategoryId, cancellationToken);
                categories[product.CategoryId] = category = (found?.Name ?? "Unknown", found is not null);
            }

            if (!units.TryGetValue(product.UnitId, out var unit))
            {
                var found = await unitRepository.GetByIdAsync(product.UnitId, cancellationToken);
                units[product.UnitId] = unit = (found?.Name ?? "Unknown", found?.Abbreviation ?? "");
            }

            var dto = DtoMapper.ToDto(product, category.Name, unit.Name, unit.Abbreviation);
            items.Add(showCost ? dto : dto with { CostPrice = null });
        }

        return Result.Success(new ProductSearchResult(items, truncated));
    }
}
