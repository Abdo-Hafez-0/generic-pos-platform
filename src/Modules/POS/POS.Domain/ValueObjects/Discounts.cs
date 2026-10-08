using Platform.Core.Amounts;
using Platform.Core.Results;

namespace POS.Domain.ValueObjects;

/// <summary>How a discount is given (FIX-08c).</summary>
public enum DiscountKind
{
    /// <summary>A percentage of the amount it applies to (10 = 10%).</summary>
    Percent = 1,

    /// <summary>A fixed amount off, tax included.</summary>
    Amount = 2
}

/// <summary>
/// A discount the cashier gave (FIX-08c): on one line, or on the whole cart. It is kept as given (10%, or 2.00 off) and turned into an
/// amount against what it applies to every time the cart is priced, so a percentage follows quantity changes and new scans, and an amount
/// is never more than what it applies to. Prices include tax: the discount amount is tax-included too.
/// </summary>
public readonly record struct DiscountRule(DiscountKind Kind, decimal Value)
{
    public static Result<DiscountRule> Create(DiscountKind kind, decimal value)
    {
        if (!Enum.IsDefined(kind))
            return Result.Failure<DiscountRule>(Error.Validation("POS.Discount.KindInvalid", "Choose a percentage or an amount."));
        if (value <= 0m)
            return Result.Failure<DiscountRule>(Error.Validation("POS.Discount.MustBePositive", "A discount must be greater than zero."));
        if (kind == DiscountKind.Percent && value > 100m)
            return Result.Failure<DiscountRule>(Error.Validation("POS.Discount.PercentTooHigh", "A discount cannot be more than 100%."));

        return Result.Success(new DiscountRule(kind, kind == DiscountKind.Amount ? TaxInclusiveLine.Round(value) : decimal.Round(value, 2, MidpointRounding.AwayFromZero)));
    }

    /// <summary>The discount amount against <paramref name="baseAmount"/>, rounded to 2 decimals and never more than it.</summary>
    public decimal AmountOf(decimal baseAmount)
    {
        if (baseAmount <= 0m) return 0m;
        var amount = Kind == DiscountKind.Percent ? TaxInclusiveLine.Round(baseAmount * Value / 100m) : Value;
        return Math.Min(amount, baseAmount);
    }

    /// <summary>The discount as a percentage of <paramref name="baseAmount"/> (for the maximum a cashier may give).</summary>
    public decimal PercentOf(decimal baseAmount)
        => Kind == DiscountKind.Percent ? Value : baseAmount <= 0m ? 100m : Value / baseAmount * 100m;
}

/// <summary>
/// Spreads a cart discount over the lines (FIX-08c, user decision: Sales stores discounts per line). Each line gets a share in proportion
/// to its amount, rounded to 2 decimals; the rounding difference goes to the largest line, so the shares always add up to the discount
/// exactly and no share is more than its line.
/// </summary>
public static class CartDiscountAllocation
{
    public static decimal[] Spread(decimal discount, IReadOnlyList<decimal> bases)
    {
        var shares = new decimal[bases.Count];
        var total = bases.Sum();
        if (discount <= 0m || total <= 0m) return shares;

        discount = Math.Min(discount, total);
        var largest = 0;
        for (var i = 1; i < bases.Count; i++)
            if (bases[i] > bases[largest]) largest = i;

        for (var i = 0; i < bases.Count; i++)
            if (i != largest) shares[i] = Math.Min(TaxInclusiveLine.Round(discount * bases[i] / total), bases[i]);

        shares[largest] = discount - shares.Sum();
        return shares;
    }
}
