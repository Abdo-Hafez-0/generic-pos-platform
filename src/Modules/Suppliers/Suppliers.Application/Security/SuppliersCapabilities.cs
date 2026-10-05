using Platform.Application.Abstractions.Authorization;

namespace Suppliers.Application.Security;

/// <summary>The capabilities the Suppliers module's operations check (declared to the platform catalog by <see cref="SuppliersCapabilityProvider"/>).</summary>
public static class SuppliersCapabilities
{
    public const string Module = "suppliers";

        public const string ManageSuppliers = "suppliers.supplier.manage";

    public static IReadOnlyList<CapabilityDescriptor> All { get; } =
    [
            new(ManageSuppliers, Module, "Manage suppliers", "Create and change suppliers, their addresses and contacts, and deactivate them.", LicenseRequirement.Module)
    ];
}

public sealed class SuppliersCapabilityProvider : ICapabilityProvider
{
    public IReadOnlyCollection<CapabilityDescriptor> GetCapabilities() => SuppliersCapabilities.All;
}
