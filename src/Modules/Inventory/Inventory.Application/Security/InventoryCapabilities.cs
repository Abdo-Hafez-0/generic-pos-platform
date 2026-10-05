using Platform.Application.Abstractions.Authorization;

namespace Inventory.Application.Security;

/// <summary>The capabilities the Inventory module's operations check (declared to the platform catalog by <see cref="InventoryCapabilityProvider"/>).</summary>
public static class InventoryCapabilities
{
    public const string Module = "inventory";

        public const string ReceiveStock = "inventory.stock.receive";
        public const string AdjustStock = "inventory.stock.adjust";
        public const string ManageLocations = "inventory.location.manage";

    public static IReadOnlyList<CapabilityDescriptor> All { get; } =
    [
            new(ReceiveStock, Module, "Receive stock", "Add received stock to a warehouse.", LicenseRequirement.Module, IsSensitive: true),
            new(AdjustStock, Module, "Adjust stock", "Correct stock levels (count differences, damage, write-offs).", LicenseRequirement.Module, IsSensitive: true),
            new(ManageLocations, Module, "Manage warehouses and locations", "Create warehouses and storage locations.", LicenseRequirement.Module)
    ];
}

public sealed class InventoryCapabilityProvider : ICapabilityProvider
{
    public IReadOnlyCollection<CapabilityDescriptor> GetCapabilities() => InventoryCapabilities.All;
}
