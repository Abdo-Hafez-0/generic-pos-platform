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
