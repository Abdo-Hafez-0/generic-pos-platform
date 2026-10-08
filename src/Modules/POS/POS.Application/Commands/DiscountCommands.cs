using System.Globalization;
using Platform.Application.Abstractions.Auditing;
using Platform.Application.Abstractions.Authorization;
using Platform.Core.Results;
using POS.Application.Abstractions;
using POS.Application.Repositories;
using POS.Domain.Entities;
using POS.Domain.Enums;
using POS.Domain.ValueObjects;

namespace POS.Application.Commands;

// ============================================================
// Discounts at the till (FIX-08c)
// ============================================================

/// <summary>
/// The most a cashier may give, as a percentage ("PosDiscount" configuration section). It applies to every discount, whether typed as a
/// percentage or as an amount (an amount is measured against what it applies to). 100 = no limit beyond the price itself.
/// </summary>
public sealed class PosDiscountOptions
{
    public const string SectionName = "PosDiscount";

    public decimal MaximumPercent { get; set; } = 100m;
}

internal static class DiscountRules
{
    /// <summary>Value 0 removes the discount; anything else must be a valid rule within the maximum against <paramref name="baseAmount"/>.</summary>
    public static Result<DiscountRule?> Read(DiscountKind kind, decimal value, decimal baseAmount, PosDiscountOptions? options)
    {
        if (value == 0m) return Result.Success<DiscountRule?>(null);

        var rule = DiscountRule.Create(kind, value);
        if (rule.IsFailure) return Result.Failure<DiscountRule?>(rule.Error);

        if (kind == DiscountKind.Amount && rule.Value.Value > baseAmount)
            return Result.Failure<DiscountRule?>(Error.Validation(
                "POS.Discount.MoreThanTheAmount", $"The discount ({Money(rule.Value.Value)}) is more than the amount it applies to ({Money(baseAmount)})."));

        var maximum = (options ?? new PosDiscountOptions()).MaximumPercent;
        if (rule.Value.PercentOf(baseAmount) > maximum)
            return Result.Failure<DiscountRule?>(Error.Validation(
                "POS.Discount.AboveMaximum", $"A discount can be at most {maximum.ToString("0.##", CultureInfo.InvariantCulture)}% here."));

        return Result.Success<DiscountRule?>(rule.Value);
    }

    public static string Money(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    public static string Describe(DiscountRule rule)
        => rule.Kind == DiscountKind.Percent ? rule.Value.ToString("0.##", CultureInfo.InvariantCulture) + "%" : Money(rule.Value);
}

/// <summary>Gives a discount on one line (percentage or amount, tax included); value 0 removes it. Needs pos.discount.give. Audited.</summary>
public sealed record SetLineDiscountCommand(Guid CartId, Guid ProductId, DiscountKind Kind, decimal Value);

public sealed class SetLineDiscountCommandHandler(
    IPosCartRepository cartRepository,
    IPosUnitOfWork unitOfWork,
    IAuthorizationService authorization,
    PosDiscountOptions? options = null,
    IBusinessEventSink? businessEvents = null)
{
    public async Task<Result> HandleAsync(SetLineDiscountCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(POS.Application.Security.POSCapabilities.GiveDiscounts, cancellationToken);
        if (allowed.IsFailure) return allowed;

        var cart = await cartRepository.GetByIdAsync(new PosCartId(command.CartId), cancellationToken);
        if (cart is null)
            return Result.Failure(Error.NotFound("POS.Discount.CartNotFound", $"Cart '{command.CartId}' was not found."));
        if (cart.Status != PosCartStatus.Open)
            return Result.Failure(Error.Conflict("POS.Discount.CartNotOpen", "A discount can only be given on an open cart."));

        var item = cart.FindItem(command.ProductId);
        if (item is null)
            return Result.Failure(Error.NotFound("POS.Cart.ItemNotFound", "Choose a line of the cart first."));

        var rule = DiscountRules.Read(command.Kind, command.Value, item.Gross, options);
        if (rule.IsFailure) return Result.Failure(rule.Error);

        var set = cart.SetLineDiscount(command.ProductId, rule.Value);
        if (set.IsFailure) return set;

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await businessEvents.TryRecordAsync(BusinessEvent.Create("pos", rule.Value is null ? "discount.line-removed" : "discount.line-given", "cart", cart.Id.Value.ToString(),
            rule.Value is { } given
                ? $"Discount of {DiscountRules.Describe(given)} ({DiscountRules.Money(item.LineDiscountAmount)}) on {item.ProductName} ({item.ProductSku}), line {DiscountRules.Money(item.Gross)}."
                : $"Line discount removed on {item.ProductName} ({item.ProductSku}).",
            $"product={item.CatalogProductId}"));
        return Result.Success();
    }
}

/// <summary>Gives a discount on the whole cart (percentage or amount, tax included); value 0 removes it. Needs pos.discount.give. Audited.</summary>
public sealed record SetCartDiscountCommand(Guid CartId, DiscountKind Kind, decimal Value);

public sealed class SetCartDiscountCommandHandler(
    IPosCartRepository cartRepository,
    IPosUnitOfWork unitOfWork,
    IAuthorizationService authorization,
    PosDiscountOptions? options = null,
    IBusinessEventSink? businessEvents = null)
{
    public async Task<Result> HandleAsync(SetCartDiscountCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(POS.Application.Security.POSCapabilities.GiveDiscounts, cancellationToken);
        if (allowed.IsFailure) return allowed;

        var cart = await cartRepository.GetByIdAsync(new PosCartId(command.CartId), cancellationToken);
        if (cart is null)
            return Result.Failure(Error.NotFound("POS.Discount.CartNotFound", $"Cart '{command.CartId}' was not found."));
        if (cart.Status != PosCartStatus.Open)
            return Result.Failure(Error.Conflict("POS.Discount.CartNotOpen", "A discount can only be given on an open cart."));

        var rule = DiscountRules.Read(command.Kind, command.Value, cart.AfterLineDiscounts, options);
        if (rule.IsFailure) return Result.Failure(rule.Error);

        var set = cart.SetCartDiscount(rule.Value);
        if (set.IsFailure) return set;

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await businessEvents.TryRecordAsync(BusinessEvent.Create("pos", rule.Value is null ? "discount.cart-removed" : "discount.cart-given", "cart", cart.Id.Value.ToString(),
            rule.Value is { } given
                ? $"Discount of {DiscountRules.Describe(given)} ({DiscountRules.Money(cart.CartDiscountAmount)}) on the whole cart of {DiscountRules.Money(cart.AfterLineDiscounts)}."
                : "Cart discount removed."));
        return Result.Success();
    }
}
