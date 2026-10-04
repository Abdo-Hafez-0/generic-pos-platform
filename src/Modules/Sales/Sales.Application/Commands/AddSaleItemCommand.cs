using Catalog.Contracts.Interfaces;
using Inventory.Contracts.Interfaces;
using Platform.Core.Results;
using Sales.Application.Abstractions;
using Sales.Application.Repositories;
using Sales.Domain.Enums;
using Sales.Domain.ValueObjects;

namespace Sales.Application.Commands;

// ============================================================
// AddSaleItemCommand
// ============================================================

/// <summary>
/// Adds a line item to a Draft Sale.
///
/// FLOW:
///   1. Load the Sale — must be in Draft status.
///   2. Validate the product exists via Catalog.Contracts (IProductLookup).
///      Never accesses CatalogDbContext or Catalog.Domain.
///   3. Optionally check stock availability via Inventory.Contracts (IStockAvailabilityChecker).
///   4. Validate and create the SaleItem with snapshot values (UnitPrice, Discount, TaxRate).
///   5. Persist atomically.
///
/// HISTORICAL DATA RULE (Architecture §18):
/// UnitPrice is supplied by the caller (from Catalog/Pricing at the time of the command).
/// This value is snapshotted into the SaleItem and never changes after the sale is completed.
///
/// Architecture: Sales.Application — no EF Core.
/// Cross-module: IProductLookup (Catalog.Contracts), IStockAvailabilityChecker (Inventory.Contracts).
/// </summary>
public sealed record AddSaleItemCommand(
    Guid SaleId,
    Guid CatalogProductId,
    decimal Quantity,
    decimal UnitPrice,
    decimal Discount = 0m,
    decimal TaxRate = 0m,
    Guid? WarehouseId = null);

public sealed class AddSaleItemCommandHandler(
    ISaleRepository saleRepository,
    IProductLookup productLookup,
    IStockAvailabilityChecker stockAvailabilityChecker,
    ISalesUnitOfWork unitOfWork)
{
    public async Task<Result<Guid>> HandleAsync(
        AddSaleItemCommand command,
        CancellationToken cancellationToken = default)
    {
        // 1. Load the sale
        var saleId = new SaleId(command.SaleId);
        var sale = await saleRepository.GetByIdAsync(saleId, cancellationToken);
        if (sale is null)
            return Result.Failure<Guid>(Error.NotFound(
                "Sales.AddSaleItem.SaleNotFound",
                $"Sale '{command.SaleId}' was not found."));

        if (sale.Status != SaleStatus.Draft)
            return Result.Failure<Guid>(Error.Conflict(
                "Sales.AddSaleItem.SaleNotDraft",
                $"Items can only be added to a Draft sale. Current status: {sale.Status}."));

        // 2. Validate product via Catalog.Contracts (never touches CatalogDbContext)
        var product = await productLookup.FindByIdAsync(command.CatalogProductId, cancellationToken);
        if (product is null)
            return Result.Failure<Guid>(Error.NotFound(
                "Sales.AddSaleItem.ProductNotFound",
                $"Catalog product '{command.CatalogProductId}' was not found."));

        if (product.Status != Catalog.Contracts.Models.ProductStatusContract.Active)
            return Result.Failure<Guid>(Error.Conflict(
                "Sales.AddSaleItem.ProductInactive",
                $"Product '{product.Name}' is not active."));

        // 3. Optionally check stock availability via Inventory.Contracts
        if (command.WarehouseId.HasValue)
        {
            var isAvailable = await stockAvailabilityChecker.IsAvailableAsync(
                command.CatalogProductId,
                command.WarehouseId.Value,
                command.Quantity,
                cancellationToken);

            if (!isAvailable)
                return Result.Failure<Guid>(Error.Conflict(
                    "Sales.AddSaleItem.InsufficientStock",
                    $"Insufficient stock for product '{product.Name}' in warehouse '{command.WarehouseId}'."));
        }

        // 4. Build value objects
        var quantityResult = SaleQuantity.Create(command.Quantity);
        if (quantityResult.IsFailure)
            return Result.Failure<Guid>(quantityResult.Error);

        var unitPriceResult = Money.Create(command.UnitPrice);
        if (unitPriceResult.IsFailure)
            return Result.Failure<Guid>(unitPriceResult.Error);

        var discountResult = Money.Create(command.Discount);
        if (discountResult.IsFailure)
            return Result.Failure<Guid>(discountResult.Error);

        // 5. Add item to sale (historical snapshot captured here)
        var addResult = sale.AddItem(
            command.CatalogProductId,
            product.Name,
            product.Sku,
            quantityResult.Value,
            unitPriceResult.Value,
            discountResult.Value,
            command.TaxRate);

        if (addResult.IsFailure)
            return Result.Failure<Guid>(addResult.Error);

        saleRepository.Update(sale);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(addResult.Value.Id.Value);
    }
}
