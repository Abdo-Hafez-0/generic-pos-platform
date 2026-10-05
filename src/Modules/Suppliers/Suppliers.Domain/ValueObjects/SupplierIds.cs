namespace Suppliers.Domain.ValueObjects;

/// <summary>Strongly-typed identifier for a Supplier.</summary>
public readonly record struct SupplierId(Guid Value)
{
    public static SupplierId New() => new(Guid.NewGuid());
    public static SupplierId Empty => new(Guid.Empty);
    public override string ToString() => Value.ToString();
}

/// <summary>Strongly-typed identifier for a SupplierAddress.</summary>
public readonly record struct SupplierAddressId(Guid Value)
{
    public static SupplierAddressId New() => new(Guid.NewGuid());
    public static SupplierAddressId Empty => new(Guid.Empty);
    public override string ToString() => Value.ToString();
}

/// <summary>Strongly-typed identifier for a SupplierContact.</summary>
public readonly record struct SupplierContactId(Guid Value)
{
    public static SupplierContactId New() => new(Guid.NewGuid());
    public static SupplierContactId Empty => new(Guid.Empty);
    public override string ToString() => Value.ToString();
}
