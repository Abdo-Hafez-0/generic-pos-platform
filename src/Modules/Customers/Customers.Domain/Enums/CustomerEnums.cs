namespace Customers.Domain.Enums;

/// <summary>Lifecycle of a customer. Customers are never deleted; they are deactivated (history stays valid).</summary>
public enum CustomerStatus
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
