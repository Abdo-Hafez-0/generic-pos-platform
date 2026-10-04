using Platform.Core.Results;

namespace POS.Domain.ValueObjects;

/// <summary>Strongly-typed identifier for a PosSession.</summary>
public readonly record struct PosSessionId(Guid Value)
{
    public static PosSessionId New() => new(Guid.NewGuid());
    public static PosSessionId Empty => new(Guid.Empty);
    public override string ToString() => Value.ToString();
}

/// <summary>Strongly-typed identifier for a PosCart.</summary>
public readonly record struct PosCartId(Guid Value)
{
    public static PosCartId New() => new(Guid.NewGuid());
    public static PosCartId Empty => new(Guid.Empty);
    public override string ToString() => Value.ToString();
}

/// <summary>Strongly-typed identifier for a PosCartItem.</summary>
public readonly record struct PosCartItemId(Guid Value)
{
    public static PosCartItemId New() => new(Guid.NewGuid());
    public static PosCartItemId Empty => new(Guid.Empty);
    public override string ToString() => Value.ToString();
}

/// <summary>A non-negative monetary amount in POS (rounded to 4 decimals, like Sales).</summary>
public readonly record struct Money(decimal Amount)
{
    public static Money Zero => new(0m);

    public static Result<Money> Create(decimal amount)
    {
        if (amount < 0m)
            return Result.Failure<Money>(Error.Validation(
                "POS.Money.NegativeAmount",
                "Monetary amount cannot be negative."));

        return Result.Success(new Money(decimal.Round(amount, 4, MidpointRounding.AwayFromZero)));
    }

    public static Money operator +(Money a, Money b) => new(a.Amount + b.Amount);
    public static Money operator *(Money a, decimal multiplier) => new(a.Amount * multiplier);

    public override string ToString() => Amount.ToString("F4");
}

/// <summary>A strictly positive quantity of a product in a cart line.</summary>
public readonly record struct CartQuantity(decimal Value)
{
    public static Result<CartQuantity> Create(decimal value)
    {
        if (value <= 0m)
            return Result.Failure<CartQuantity>(Error.Validation(
                "POS.CartQuantity.MustBePositive",
                "Cart quantity must be greater than zero."));

        return Result.Success(new CartQuantity(value));
    }

    public static CartQuantity operator +(CartQuantity a, CartQuantity b) => new(a.Value + b.Value);

    public override string ToString() => Value.ToString();
}
