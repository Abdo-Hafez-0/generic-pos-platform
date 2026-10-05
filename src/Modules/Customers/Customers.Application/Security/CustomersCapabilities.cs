using Platform.Application.Abstractions.Authorization;

namespace Customers.Application.Security;

/// <summary>The capabilities the Customers module's operations check (declared to the platform catalog by <see cref="CustomersCapabilityProvider"/>).</summary>
public static class CustomersCapabilities
{
    public const string Module = "customers";

        public const string ManageCustomers = "customers.customer.manage";

    public static IReadOnlyList<CapabilityDescriptor> All { get; } =
    [
            new(ManageCustomers, Module, "Manage customers", "Create and change customers, their addresses and contacts, and deactivate them.", LicenseRequirement.Module)
    ];
}

public sealed class CustomersCapabilityProvider : ICapabilityProvider
{
    public IReadOnlyCollection<CapabilityDescriptor> GetCapabilities() => CustomersCapabilities.All;
}
