namespace Customers.Domain.ValueObjects;

/// <summary>Strongly-typed identifier for a Customer.</summary>
public readonly record struct CustomerId(Guid Value)
{
    public static CustomerId New() => new(Guid.NewGuid());
    public static CustomerId Empty => new(Guid.Empty);
    public override string ToString() => Value.ToString();
}

/// <summary>Strongly-typed identifier for a CustomerAddress.</summary>
public readonly record struct CustomerAddressId(Guid Value)
{
    public static CustomerAddressId New() => new(Guid.NewGuid());
    public static CustomerAddressId Empty => new(Guid.Empty);
    public override string ToString() => Value.ToString();
}

/// <summary>Strongly-typed identifier for a CustomerContact.</summary>
public readonly record struct CustomerContactId(Guid Value)
{
    public static CustomerContactId New() => new(Guid.NewGuid());
    public static CustomerContactId Empty => new(Guid.Empty);
    public override string ToString() => Value.ToString();
}
