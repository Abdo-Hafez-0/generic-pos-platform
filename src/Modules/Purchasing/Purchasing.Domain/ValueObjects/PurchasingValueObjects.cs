using Platform.Core.Results;

namespace Purchasing.Domain.ValueObjects;

public readonly record struct PurchaseOrderId(Guid Value)
{
    public static PurchaseOrderId New() => new(Guid.NewGuid());
    public static PurchaseOrderId Empty => new(Guid.Empty);
    public override string ToString() => Value.ToString();
}

public readonly record struct PurchaseOrderLineId(Guid Value)
{
    public static PurchaseOrderLineId New() => new(Guid.NewGuid());
    public static PurchaseOrderLineId Empty => new(Guid.Empty);
    public override string ToString() => Value.ToString();
}

/// <summary>A strictly positive quantity ordered.</summary>
public readonly record struct OrderQuantity(decimal Value)
{
    public static Result<OrderQuantity> Create(decimal value)
        => value <= 0m
            ? Result.Failure<OrderQuantity>(Error.Validation("Purchasing.PurchaseOrder.InvalidQuantity", "The quantity must be greater than zero."))
            : Result.Success(new OrderQuantity(value));
}

/// <summary>A non-negative monetary amount (rounded to 4 decimals).</summary>
public readonly record struct Money(decimal Amount)
{
    public static Money Zero => new(0m);

    public static Result<Money> Create(decimal amount)
        => amount < 0m
            ? Result.Failure<Money>(Error.Validation("Purchasing.PurchaseOrder.InvalidCost", "A cost cannot be negative."))
            : Result.Success(new Money(decimal.Round(amount, 4, MidpointRounding.AwayFromZero)));

    public static Money operator +(Money a, Money b) => new(a.Amount + b.Amount);
    public static Money operator *(Money a, decimal m) => new(a.Amount * m);
}
