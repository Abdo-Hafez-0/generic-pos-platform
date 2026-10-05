using Platform.Application.Abstractions.Authorization;
using Catalog.Contracts.Interfaces;
using Catalog.Contracts.Models;
using Platform.Core.Results;
using Pricing.Application.Abstractions;
using Pricing.Application.Repositories;
using Pricing.Domain.Entities;
using Pricing.Domain.ValueObjects;

namespace Pricing.Application.Commands;

// ============================================================
// Price lists
// ============================================================

/// <summary>Creates a price list. The first list ever created becomes the default automatically.</summary>
public sealed record CreatePriceListCommand(string Code, string Name, bool MakeDefault = false);

public sealed class CreatePriceListCommandHandler(IPriceListRepository lists, IPricingUnitOfWork unitOfWork, IAuthorizationService authorization)
{
    public async Task<Result<Guid>> HandleAsync(CreatePriceListCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(Pricing.Application.Security.PricingCapabilities.ManagePriceLists, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<Guid>(allowed.Error);

        var existing = await lists.ListAsync(cancellationToken);
        var makeDefault = command.MakeDefault || existing.Count == 0;

        var created = PriceList.Create(command.Code, command.Name, makeDefault);
        if (created.IsFailure) return Result.Failure<Guid>(created.Error);

        if (existing.Any(l => l.Code == created.Value.Code))
            return Result.Failure<Guid>(Error.Conflict("Pricing.CreatePriceList.DuplicateCode", $"A price list with code '{created.Value.Code}' already exists."));

        if (makeDefault)
            foreach (var list in existing.Where(l => l.IsDefault)) list.ClearDefault();

        await lists.AddAsync(created.Value, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success(created.Value.Id.Value);
    }
}

public sealed record SetDefaultPriceListCommand(Guid PriceListId);

public sealed class SetDefaultPriceListCommandHandler(IPriceListRepository lists, IPricingUnitOfWork unitOfWork, IAuthorizationService authorization)
{
    public async Task<Result> HandleAsync(SetDefaultPriceListCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(Pricing.Application.Security.PricingCapabilities.ManagePriceLists, cancellationToken);
        if (allowed.IsFailure) return allowed;

        var all = await lists.ListAsync(cancellationToken);
        var target = all.FirstOrDefault(l => l.Id == new PriceListId(command.PriceListId));
        if (target is null)
            return Result.Failure(Error.NotFound("Pricing.SetDefault.PriceListNotFound", $"Price list '{command.PriceListId}' was not found."));

        var made = target.MakeDefault();
        if (made.IsFailure) return made;

        foreach (var other in all.Where(l => l.Id != target.Id)) other.ClearDefault();
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

public sealed record DeactivatePriceListCommand(Guid PriceListId);

public sealed class DeactivatePriceListCommandHandler(IPriceListRepository lists, IPricingUnitOfWork unitOfWork, IAuthorizationService authorization)
{
    public async Task<Result> HandleAsync(DeactivatePriceListCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(Pricing.Application.Security.PricingCapabilities.ManagePriceLists, cancellationToken);
        if (allowed.IsFailure) return allowed;

        var list = await lists.GetByIdAsync(new PriceListId(command.PriceListId), cancellationToken);
        if (list is null)
            return Result.Failure(Error.NotFound("Pricing.DeactivatePriceList.PriceListNotFound", $"Price list '{command.PriceListId}' was not found."));

        var result = list.Deactivate();
        if (result.IsFailure) return result;

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

// ============================================================
// Prices
// ============================================================

internal static class PriceChecks
{
    /// <summary>Rejects an active price whose validity overlaps another ACTIVE price of the same product, list and quantity break.</summary>
    public static async Task<Error?> CheckNoOverlapAsync(IPriceRepository prices, Price candidate, CancellationToken cancellationToken)
    {
        var siblings = await prices.ListForProductAsync(candidate.ProductId, candidate.PriceListId, cancellationToken);
        var clash = siblings.FirstOrDefault(p => p.Status == Domain.Enums.PriceStatus.Active && candidate.OverlapsWith(p));
        return clash is null
            ? null
            : Error.Conflict("Pricing.Price.Overlap",
                $"An active price for this product, price list and quantity break already covers this period (price '{clash.Id.Value}').");
    }
}

/// <summary>
/// Creates a price. The product (SKU or Catalog ID) is validated through Catalog.Contracts. When no price list is given the
/// default list is used (it must exist).
/// </summary>
public sealed record CreatePriceCommand(
    string ProductCode, decimal Amount, DateTime EffectiveFrom, DateTime? EffectiveTo = null, decimal MinimumQuantity = 1m, Guid? PriceListId = null);

public sealed class CreatePriceCommandHandler(
    IPriceRepository prices,
    IPriceListRepository lists,
    IProductLookup productLookup,
    IPricingUnitOfWork unitOfWork,
    IAuthorizationService authorization)
{
    public async Task<Result<Guid>> HandleAsync(CreatePriceCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(Pricing.Application.Security.PricingCapabilities.ManagePrices, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<Guid>(allowed.Error);

        if (string.IsNullOrWhiteSpace(command.ProductCode))
            return Result.Failure<Guid>(Error.Validation("Pricing.CreatePrice.ProductCodeRequired", "A product SKU (or ID) is required."));

        var code = command.ProductCode.Trim();
        ProductLookupResult? product = Guid.TryParse(code, out var productId)
            ? await productLookup.FindByIdAsync(productId, cancellationToken)
            : null;
        product ??= await productLookup.FindBySkuAsync(code, cancellationToken);
        if (product is null)
            return Result.Failure<Guid>(Error.NotFound("Pricing.CreatePrice.ProductNotFound", $"No product found for '{code}'."));

        var list = command.PriceListId is { } id
            ? await lists.GetByIdAsync(new PriceListId(id), cancellationToken)
            : await lists.GetDefaultAsync(cancellationToken);
        if (list is null)
            return Result.Failure<Guid>(Error.NotFound("Pricing.CreatePrice.PriceListNotFound", "The price list was not found (create a default price list first)."));
        if (list.Status != Domain.Enums.PriceListStatus.Active)
            return Result.Failure<Guid>(Error.Conflict("Pricing.CreatePrice.PriceListInactive", "Prices cannot be added to an inactive price list."));

        var created = Price.Create(list.Id, product.ProductId, command.Amount, command.MinimumQuantity, command.EffectiveFrom, command.EffectiveTo);
        if (created.IsFailure) return Result.Failure<Guid>(created.Error);

        var overlap = await PriceChecks.CheckNoOverlapAsync(prices, created.Value, cancellationToken);
        if (overlap is not null) return Result.Failure<Guid>(overlap);

        await prices.AddAsync(created.Value, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success(created.Value.Id.Value);
    }
}

public sealed record UpdatePriceCommand(Guid PriceId, decimal Amount, DateTime EffectiveFrom, DateTime? EffectiveTo = null, decimal MinimumQuantity = 1m);

public sealed class UpdatePriceCommandHandler(IPriceRepository prices, IPricingUnitOfWork unitOfWork, IAuthorizationService authorization)
{
    public async Task<Result> HandleAsync(UpdatePriceCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(Pricing.Application.Security.PricingCapabilities.ManagePrices, cancellationToken);
        if (allowed.IsFailure) return allowed;

        var price = await prices.GetByIdAsync(new PriceId(command.PriceId), cancellationToken);
        if (price is null)
            return Result.Failure(Error.NotFound("Pricing.UpdatePrice.PriceNotFound", $"Price '{command.PriceId}' was not found."));

        var updated = price.Update(command.Amount, command.MinimumQuantity, command.EffectiveFrom, command.EffectiveTo);
        if (updated.IsFailure) return updated;

        var overlap = await PriceChecks.CheckNoOverlapAsync(prices, price, cancellationToken);
        if (overlap is not null) return Result.Failure(overlap);   // nothing is saved: the tracked change is discarded with the scope

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

public sealed record DeactivatePriceCommand(Guid PriceId);

public sealed class DeactivatePriceCommandHandler(IPriceRepository prices, IPricingUnitOfWork unitOfWork, IAuthorizationService authorization)
{
    public async Task<Result> HandleAsync(DeactivatePriceCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(Pricing.Application.Security.PricingCapabilities.ManagePrices, cancellationToken);
        if (allowed.IsFailure) return allowed;

        var price = await prices.GetByIdAsync(new PriceId(command.PriceId), cancellationToken);
        if (price is null)
            return Result.Failure(Error.NotFound("Pricing.DeactivatePrice.PriceNotFound", $"Price '{command.PriceId}' was not found."));

        var result = price.Deactivate();
        if (result.IsFailure) return result;

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
