using Platform.Core.Results;

namespace Sales.Domain.ValueObjects;

/// <summary>
/// Represents a quantity of items in a sale line.
/// Must be strictly positive (> 0) for a SaleItem.
///
/// Architecture: Sales.Domain — no EF Core, no WPF, no HTTP.
/// </summary>
public readonly record struct SaleQuantity(decimal Value)
{
    public static SaleQuantity One => new(1m);

    /// <summary>Creates a SaleQuantity. Value must be strictly positive.</summary>
    public static Result<SaleQuantity> Create(decimal value)
    {
        if (value <= 0m)
            return Result.Failure<SaleQuantity>(Error.Validation(
                "Sales.SaleQuantity.MustBePositive",
                "Sale quantity must be greater than zero."));

        return Result.Success(new SaleQuantity(value));
    }

    /// <summary>For EF Core materialisation only.</summary>
    internal static SaleQuantity FromRaw(decimal value) => new(value);

    public static bool operator >(SaleQuantity a, SaleQuantity b) => a.Value > b.Value;
    public static bool operator <(SaleQuantity a, SaleQuantity b) => a.Value < b.Value;

    public override string ToString() => Value.ToString();
}
