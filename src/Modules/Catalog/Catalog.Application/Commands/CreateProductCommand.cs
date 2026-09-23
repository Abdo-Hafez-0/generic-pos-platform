using Catalog.Domain.Entities;
using Catalog.Domain.Enums;
using Catalog.Domain.ValueObjects;
using Catalog.Application.Repositories;
using Catalog.Application.Abstractions;
using Platform.Core.Results;

namespace Catalog.Application.Commands;

// ============================================================
// CreateProductCommand
// ============================================================

public sealed record CreateProductCommand(
    string Sku,
    string Name,
    Guid CategoryId,
    Guid UnitId,
    decimal SalePrice,
    decimal? CostPrice = null,
    string? Description = null);

public sealed class CreateProductCommandHandler(
    IProductRepository productRepository,
    ICategoryRepository categoryRepository,
    IUnitRepository unitRepository,
    ICatalogUnitOfWork unitOfWork)
{
    public async Task<Result<ProductId>> HandleAsync(
        CreateProductCommand command,
        CancellationToken cancellationToken = default)
    {
        // Verify category exists
        var categoryId = new CategoryId(command.CategoryId);
        if (!await categoryRepository.ExistsAsync(categoryId, cancellationToken))
            return Result.Failure<ProductId>(
                Error.NotFound("Catalog.Product.CategoryNotFound", $"Category '{command.CategoryId}' was not found."));

        // Verify unit exists
        var unitId = new UnitId(command.UnitId);
        if (!await unitRepository.ExistsAsync(unitId, cancellationToken))
            return Result.Failure<ProductId>(
                Error.NotFound("Catalog.Product.UnitNotFound", $"Unit '{command.UnitId}' was not found."));

        // Guard duplicate SKU
        if (await productRepository.ExistsBySkuAsync(command.Sku, cancellationToken))
            return Result.Failure<ProductId>(
                Error.Conflict("Catalog.Product.DuplicateSku", $"A product with SKU '{command.Sku}' already exists."));

        // Create the aggregate (enforces invariants)
        var createResult = Product.Create(
            command.Sku,
            command.Name,
            categoryId,
            unitId,
            command.SalePrice,
            command.CostPrice,
            command.Description);

        if (createResult.IsFailure)
            return Result.Failure<ProductId>(createResult.Error);

        var product = createResult.Value;
        await productRepository.AddAsync(product, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(product.Id);
    }
}
