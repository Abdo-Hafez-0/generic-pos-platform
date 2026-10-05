namespace Suppliers.Domain.Enums;

/// <summary>Lifecycle of a supplier. Suppliers are never deleted; they are deactivated (history stays valid).</summary>
public enum SupplierStatus
{
    Active = 1,
    Inactive = 2
}

public enum AddressType
{
    Billing = 1,
    Shipping = 2,
    Other = 3
}
