using Catalog.Domain.ValueObjects;
using Catalog.Application.Repositories;
using Catalog.Application.Abstractions;
using Platform.Core.Results;

namespace Catalog.Application.Commands;

// ============================================================
// UpdateProductCommand
// ============================================================

public sealed record UpdateProductCommand(
    Guid ProductId,
    string Name,
    Guid CategoryId,
    Guid UnitId,
    decimal SalePrice,
    decimal? CostPrice = null,
    string? Description = null);

public sealed class UpdateProductCommandHandler(
    IProductRepository productRepository,
    ICategoryRepository categoryRepository,
    IUnitRepository unitRepository,
    ICatalogUnitOfWork unitOfWork)
{
    public async Task<Result> HandleAsync(
        UpdateProductCommand command,
        CancellationToken cancellationToken = default)
    {
        var product = await productRepository.GetByIdAsync(new ProductId(command.ProductId), cancellationToken);
        if (product is null)
            return Result.Failure(Error.NotFound("Catalog.Product.NotFound", $"Product '{command.ProductId}' was not found."));

        var categoryId = new CategoryId(command.CategoryId);
        if (!await categoryRepository.ExistsAsync(categoryId, cancellationToken))
            return Result.Failure(Error.NotFound("Catalog.Product.CategoryNotFound", $"Category '{command.CategoryId}' was not found."));

        var unitId = new UnitId(command.UnitId);
        if (!await unitRepository.ExistsAsync(unitId, cancellationToken))
            return Result.Failure(Error.NotFound("Catalog.Product.UnitNotFound", $"Unit '{command.UnitId}' was not found."));

        var updateResult = product.Update(command.Name, categoryId, unitId, command.SalePrice, command.CostPrice, command.Description);
        if (updateResult.IsFailure)
            return updateResult;

        productRepository.Update(product);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
