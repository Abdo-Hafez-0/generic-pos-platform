using Catalog.Domain.ValueObjects;
using Catalog.Application.Repositories;
using Catalog.Application.Abstractions;
using Platform.Core.Results;

namespace Catalog.Application.Commands;

// ============================================================
// DeactivateProductCommand
// ============================================================

public sealed record DeactivateProductCommand(Guid ProductId);

public sealed class DeactivateProductCommandHandler(
    IProductRepository productRepository,
    ICatalogUnitOfWork unitOfWork)
{
    public async Task<Result> HandleAsync(
        DeactivateProductCommand command,
        CancellationToken cancellationToken = default)
    {
        var product = await productRepository.GetByIdAsync(new ProductId(command.ProductId), cancellationToken);
        if (product is null)
            return Result.Failure(Error.NotFound("Catalog.Product.NotFound", $"Product '{command.ProductId}' was not found."));

        var result = product.Deactivate();
        if (result.IsFailure) return result;

        productRepository.Update(product);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
