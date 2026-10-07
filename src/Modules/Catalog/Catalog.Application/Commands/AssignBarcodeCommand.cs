using Platform.Application.Abstractions.Authorization;
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

/// <summary>
/// Adds a barcode to a product. A barcode identifies ONE product: a value already carried by another product is refused, because a
/// scan at the till must never pick one of two products at random (FIX-01c).
/// </summary>
public sealed class AssignBarcodeCommandHandler(
    IProductRepository productRepository,
    IBarcodeRepository barcodeRepository,
    ICatalogUnitOfWork unitOfWork,
    IAuthorizationService authorization)
{
    public async Task<Result<BarcodeId>> HandleAsync(
        AssignBarcodeCommand command,
        CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(Catalog.Application.Security.CatalogCapabilities.EditProduct, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<BarcodeId>(allowed.Error);

        var product = await productRepository.GetByIdAsync(new ProductId(command.ProductId), cancellationToken);
        if (product is null)
            return Result.Failure<BarcodeId>(
                Error.NotFound("Catalog.Product.NotFound", $"Product '{command.ProductId}' was not found."));

        if (!string.IsNullOrWhiteSpace(command.BarcodeValue)
            && await barcodeRepository.GetProductByBarcodeValueAsync(command.BarcodeValue, cancellationToken) is { } owner
            && owner.Id != product.Id)
            return Result.Failure<BarcodeId>(Error.Conflict(
                "Catalog.Barcode.InUse", $"The barcode '{command.BarcodeValue.Trim()}' already belongs to product {owner.Sku} ({owner.Name})."));

        var result = product.AssignBarcode(command.BarcodeValue, command.Format);
        if (result.IsFailure)
            return Result.Failure<BarcodeId>(result.Error);

        productRepository.Update(product);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success(result.Value.Id);
    }
}
