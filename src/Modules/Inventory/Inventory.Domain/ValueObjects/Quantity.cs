using Platform.Core.Results;

namespace Inventory.Domain.ValueObjects;

/// <summary>
/// Represents a non-negative stock quantity.
///
/// Using decimal underlying type so that future weighted/fractional inventory
/// (e.g. kg, litres, metres) can be supported without breaking changes.
///
/// INVARIANT: Quantity cannot be negative. Zero is allowed (empty stock location).
/// </summary>
public readonly record struct Quantity
{
    /// <summary>The underlying decimal value.</summary>
    public decimal Value { get; }

    private Quantity(decimal value) => Value = value;

    /// <summary>Zero quantity.</summary>
    public static readonly Quantity Zero = new(0m);

    /// <summary>Creates a Quantity, enforcing the non-negative invariant.</summary>
    public static Result<Quantity> Create(decimal value)
    {
        if (value < 0)
            return Result.Failure<Quantity>(
                Error.Validation(
                    "Inventory.Quantity.Negative",
                    $"Quantity cannot be negative. Received: {value}."));

        return Result.Success(new Quantity(value));
    }

    /// <summary>
    /// Creates a Quantity without validation — for use by Infrastructure when
    /// loading from a trusted persistence store.
    /// </summary>
    public static Quantity FromTrusted(decimal value) => new(value);

    /// <summary>Adds another quantity, returning a new Quantity.</summary>
    public Quantity Add(Quantity other) => new(Value + other.Value);

    /// <summary>
    /// Subtracts another quantity. Returns a failure if the result would be negative.
    /// Callers are responsible for checking business rules before subtracting.
    /// </summary>
    public Result<Quantity> Subtract(Quantity other)
    {
        var result = Value - other.Value;
        if (result < 0)
            return Result.Failure<Quantity>(
                Error.Conflict(
                    "Inventory.Quantity.InsufficientStock",
                    $"Insufficient stock: cannot subtract {other.Value} from {Value}."));

        return Result.Success(new Quantity(result));
    }

    public override string ToString() => Value.ToString("G");

    public static bool operator >(Quantity left, Quantity right) => left.Value > right.Value;
    public static bool operator <(Quantity left, Quantity right) => left.Value < right.Value;
    public static bool operator >=(Quantity left, Quantity right) => left.Value >= right.Value;
    public static bool operator <=(Quantity left, Quantity right) => left.Value <= right.Value;
}
