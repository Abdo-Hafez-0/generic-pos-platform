using Platform.Application.Abstractions.Authorization;

namespace Customers.Application.Security;

/// <summary>The capabilities the Customers module's operations check (declared to the platform catalog by <see cref="CustomersCapabilityProvider"/>).</summary>
public static class CustomersCapabilities
{
    public const string Module = "customers";

    public const string ManageCustomers = "customers.customer.manage";

    /// <summary>Customers are personal data (names, e-mail, phone, addresses): reading them is a capability of its own.</summary>
    public const string ViewCustomers = "customers.customer.view";

    public static IReadOnlyList<CapabilityDescriptor> All { get; } =
    [
        new(ManageCustomers, Module, "Manage customers", "Create and change customers, their addresses and contacts, and deactivate them.", LicenseRequirement.Module),
        new(ViewCustomers, Module, "View customers", "See customer records, including their contact details and addresses (personal data). Available in every license state: the data belongs to the customer.", LicenseRequirement.None, IsSensitive: true)
    ];
}

public sealed class CustomersCapabilityProvider : ICapabilityProvider
{
    public IReadOnlyCollection<CapabilityDescriptor> GetCapabilities() => CustomersCapabilities.All;
}
