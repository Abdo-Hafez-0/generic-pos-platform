using Catalog.Domain.Enums;
using Catalog.Domain.ValueObjects;
using Catalog.Application.Repositories;
using Catalog.Application.Abstractions;
using Platform.Core.Results;

namespace Catalog.Application.Commands;

// ============================================================
// AssignBarcodeCommand
// ============================================================

public sealed record AssignBarcodeCommand(
    Guid ProductId,
    string BarcodeValue,
    BarcodeFormat Format = BarcodeFormat.EAN13);

public sealed class AssignBarcodeCommandHandler(
    IProductRepository productRepository,
    ICatalogUnitOfWork unitOfWork)
{
    public async Task<Result<BarcodeId>> HandleAsync(
        AssignBarcodeCommand command,
        CancellationToken cancellationToken = default)
    {
        var product = await productRepository.GetByIdAsync(new ProductId(command.ProductId), cancellationToken);
        if (product is null)
            return Result.Failure<BarcodeId>(
                Error.NotFound("Catalog.Product.NotFound", $"Product '{command.ProductId}' was not found."));

        var result = product.AssignBarcode(command.BarcodeValue, command.Format);
        if (result.IsFailure)
            return Result.Failure<BarcodeId>(result.Error);

        productRepository.Update(product);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success(result.Value.Id);
    }
}
