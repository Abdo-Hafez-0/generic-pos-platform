using Platform.Core.Results;

namespace Sales.Domain.ValueObjects;

/// <summary>
/// Represents a monetary amount in the Sales domain.
///
/// Money is always non-negative in a sales context.
/// Monetary operations such as discounts and taxes are applied as separate values.
///
/// Architecture: Sales.Domain — no EF Core, no WPF, no HTTP.
/// </summary>
public readonly record struct Money(decimal Amount)
{
    public static Money Zero => new(0m);

    /// <summary>Creates a Money value. Amount must be >= 0.</summary>
    public static Result<Money> Create(decimal amount)
    {
        if (amount < 0m)
            return Result.Failure<Money>(Error.Validation(
                "Sales.Money.NegativeAmount",
                "Monetary amount cannot be negative."));

        return Result.Success(new Money(decimal.Round(amount, 4, MidpointRounding.AwayFromZero)));
    }

    /// <summary>Creates a Money value without validation — for EF Core materialisation only.</summary>
    internal static Money FromRaw(decimal amount) => new(amount);

    public static Money operator +(Money a, Money b) => new(a.Amount + b.Amount);
    public static Money operator -(Money a, Money b) => new(a.Amount - b.Amount);
    public static Money operator *(Money a, decimal multiplier) => new(a.Amount * multiplier);

    public static bool operator >(Money a, Money b) => a.Amount > b.Amount;
    public static bool operator <(Money a, Money b) => a.Amount < b.Amount;
    public static bool operator >=(Money a, Money b) => a.Amount >= b.Amount;
    public static bool operator <=(Money a, Money b) => a.Amount <= b.Amount;

    public override string ToString() => Amount.ToString("F4");
}
