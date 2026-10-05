using Catalog.Contracts.Interfaces;
using Catalog.Contracts.Models;
using Inventory.Contracts.Interfaces;
using Platform.Core.Results;
using Pricing.Contracts.Interfaces;
using POS.Application.Abstractions;
using POS.Application.Repositories;
using POS.Domain.Entities;
using POS.Domain.Enums;
using POS.Domain.ValueObjects;

namespace POS.Application.Commands;

// ============================================================
// StartCartCommand
// ============================================================

/// <summary>Starts a cart for an open session, or returns the session's existing open cart.</summary>
public sealed record StartCartCommand(Guid SessionId);

public sealed class StartCartCommandHandler(
    IPosSessionRepository sessionRepository,
    IPosCartRepository cartRepository,
    IPosUnitOfWork unitOfWork)
{
    public async Task<Result<Guid>> HandleAsync(
        StartCartCommand command,
        CancellationToken cancellationToken = default)
    {
        var sessionId = new PosSessionId(command.SessionId);
        var session = await sessionRepository.GetByIdAsync(sessionId, cancellationToken);
        if (session is null)
            return Result.Failure<Guid>(Error.NotFound(
                "POS.StartCart.SessionNotFound",
                $"POS session '{command.SessionId}' was not found."));

        if (session.Status != PosSessionStatus.Open)
            return Result.Failure<Guid>(Error.Conflict(
                "POS.StartCart.SessionNotOpen",
                "A cart can only be started on an open POS session."));

        var existing = await cartRepository.GetOpenCartForSessionAsync(sessionId, cancellationToken);
        if (existing is not null)
            return Result.Success(existing.Id.Value);

        var cartResult = PosCart.Start(sessionId);
        if (cartResult.IsFailure)
            return Result.Failure<Guid>(cartResult.Error);

        await cartRepository.AddAsync(cartResult.Value, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(cartResult.Value.Id.Value);
    }
}

// ============================================================
// AddProductToCartCommand
// ============================================================

/// <summary>
/// Adds a product to the cart. The product is identified by barcode first, then by SKU
/// (both through Catalog.Contracts). Stock for the requested TOTAL quantity in the cart is validated
/// through Inventory.Contracts against the session's warehouse. The Catalog sale price is snapshotted.
/// </summary>
public sealed record AddProductToCartCommand(Guid CartId, string ProductCode, decimal Quantity = 1m);

public sealed class AddProductToCartCommandHandler(
    IPosCartRepository cartRepository,
    IPosSessionRepository sessionRepository,
    IProductBarcodeResolver barcodeResolver,
    IProductLookup productLookup,
    IStockAvailabilityChecker stockAvailabilityChecker,
    IPosUnitOfWork unitOfWork,
    IPriceResolver? priceResolver = null)
{
    public async Task<Result<Guid>> HandleAsync(
        AddProductToCartCommand command,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.ProductCode))
            return Result.Failure<Guid>(Error.Validation(
                "POS.AddProduct.CodeRequired", "A barcode or SKU is required."));

        var quantityResult = CartQuantity.Create(command.Quantity);
        if (quantityResult.IsFailure)
            return Result.Failure<Guid>(quantityResult.Error);

        var cart = await cartRepository.GetByIdAsync(new PosCartId(command.CartId), cancellationToken);
        if (cart is null)
            return Result.Failure<Guid>(Error.NotFound(
                "POS.AddProduct.CartNotFound", $"Cart '{command.CartId}' was not found."));

        if (cart.Status != PosCartStatus.Open)
            return Result.Failure<Guid>(Error.Conflict(
                "POS.AddProduct.CartNotOpen", "Products can only be added to an open cart."));

        var session = await sessionRepository.GetByIdAsync(cart.SessionId, cancellationToken);
        if (session is null || session.Status != PosSessionStatus.Open)
            return Result.Failure<Guid>(Error.Conflict(
                "POS.AddProduct.SessionNotOpen", "The cart's POS session is not open."));

        var code = command.ProductCode.Trim();
        var product = await barcodeResolver.ResolveAsync(code, cancellationToken)
                      ?? await productLookup.FindBySkuAsync(code, cancellationToken);
        if (product is null)
            return Result.Failure<Guid>(Error.NotFound(
                "POS.AddProduct.ProductNotFound", $"No product found for barcode or SKU '{code}'."));

        if (product.Status != ProductStatusContract.Active)
            return Result.Failure<Guid>(Error.Conflict(
                "POS.AddProduct.ProductInactive", $"Product '{product.Name}' is not active."));

        var alreadyInCart = cart.FindItem(product.ProductId)?.Quantity.Value ?? 0m;
        var requiredTotal = alreadyInCart + quantityResult.Value.Value;
        var inStock = await stockAvailabilityChecker.IsAvailableAsync(
            product.ProductId, session.WarehouseId, requiredTotal, cancellationToken);
        if (!inStock)
            return Result.Failure<Guid>(Error.Conflict(
                "POS.AddProduct.InsufficientStock",
                $"Insufficient stock for '{product.Name}' (requested total: {requiredTotal})."));

        // OPTIONAL Pricing integration: when the Pricing module is installed and has an applicable price it wins over the Catalog
        // sale price; otherwise the Catalog price is used. The resolved amount is snapshotted on the cart line (a merge keeps the
        // first line's snapshot). POS works unchanged when Pricing is absent.
        var unitPrice = product.SalePrice;
        if (priceResolver is not null)
        {
            var resolved = await priceResolver.ResolveAsync(product.ProductId, quantityResult.Value.Value, null, null, cancellationToken);
            if (resolved.Found) unitPrice = resolved.Amount;
        }

        var priceResult = Money.Create(unitPrice);
        if (priceResult.IsFailure)
            return Result.Failure<Guid>(priceResult.Error);

        var addResult = cart.AddItem(
            product.ProductId, product.Sku, product.Name, quantityResult.Value, priceResult.Value);
        if (addResult.IsFailure)
            return Result.Failure<Guid>(addResult.Error);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success(addResult.Value.Id.Value);
    }
}

// ============================================================
// RemoveProductFromCartCommand
// ============================================================

public sealed record RemoveProductFromCartCommand(Guid CartId, Guid ProductId);

public sealed class RemoveProductFromCartCommandHandler(
    IPosCartRepository cartRepository,
    IPosUnitOfWork unitOfWork)
{
    public async Task<Result> HandleAsync(
        RemoveProductFromCartCommand command,
        CancellationToken cancellationToken = default)
    {
        var cart = await cartRepository.GetByIdAsync(new PosCartId(command.CartId), cancellationToken);
        if (cart is null)
            return Result.Failure(Error.NotFound(
                "POS.RemoveProduct.CartNotFound", $"Cart '{command.CartId}' was not found."));

        var result = cart.RemoveItem(command.ProductId);
        if (result.IsFailure)
            return result;

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

// ============================================================
// ChangeCartQuantityCommand
// ============================================================

/// <summary>Sets the quantity of an existing cart line. Stock is re-validated for the new quantity.</summary>
public sealed record ChangeCartQuantityCommand(Guid CartId, Guid ProductId, decimal Quantity);

public sealed class ChangeCartQuantityCommandHandler(
    IPosCartRepository cartRepository,
    IPosSessionRepository sessionRepository,
    IStockAvailabilityChecker stockAvailabilityChecker,
    IPosUnitOfWork unitOfWork)
{
    public async Task<Result> HandleAsync(
        ChangeCartQuantityCommand command,
        CancellationToken cancellationToken = default)
    {
        var quantityResult = CartQuantity.Create(command.Quantity);
        if (quantityResult.IsFailure)
            return Result.Failure(quantityResult.Error);

        var cart = await cartRepository.GetByIdAsync(new PosCartId(command.CartId), cancellationToken);
        if (cart is null)
            return Result.Failure(Error.NotFound(
                "POS.ChangeQuantity.CartNotFound", $"Cart '{command.CartId}' was not found."));

        if (cart.Status != PosCartStatus.Open)
            return Result.Failure(Error.Conflict(
                "POS.ChangeQuantity.CartNotOpen", "Quantities can only be changed on an open cart."));

        if (cart.FindItem(command.ProductId) is null)
            return Result.Failure(Error.NotFound(
                "POS.Cart.ItemNotFound", $"Product '{command.ProductId}' is not in the cart."));

        var session = await sessionRepository.GetByIdAsync(cart.SessionId, cancellationToken);
        if (session is null || session.Status != PosSessionStatus.Open)
            return Result.Failure(Error.Conflict(
                "POS.ChangeQuantity.SessionNotOpen", "The cart's POS session is not open."));

        var inStock = await stockAvailabilityChecker.IsAvailableAsync(
            command.ProductId, session.WarehouseId, quantityResult.Value.Value, cancellationToken);
        if (!inStock)
            return Result.Failure(Error.Conflict(
                "POS.ChangeQuantity.InsufficientStock",
                $"Insufficient stock for the requested quantity ({quantityResult.Value.Value})."));

        var result = cart.ChangeQuantity(command.ProductId, quantityResult.Value);
        if (result.IsFailure)
            return result;

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

// ============================================================
// ClearCartCommand
// ============================================================

public sealed record ClearCartCommand(Guid CartId);

public sealed class ClearCartCommandHandler(
    IPosCartRepository cartRepository,
    IPosUnitOfWork unitOfWork)
{
    public async Task<Result> HandleAsync(
        ClearCartCommand command,
        CancellationToken cancellationToken = default)
    {
        var cart = await cartRepository.GetByIdAsync(new PosCartId(command.CartId), cancellationToken);
        if (cart is null)
            return Result.Failure(Error.NotFound(
                "POS.ClearCart.CartNotFound", $"Cart '{command.CartId}' was not found."));

        var result = cart.Clear();
        if (result.IsFailure)
            return result;

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
