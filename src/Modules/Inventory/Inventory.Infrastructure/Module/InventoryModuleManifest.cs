using Platform.Core.Modules;

namespace Inventory.Infrastructure.Module;

/// <summary>
/// The Inventory module's manifest — describes its identity, version, and capabilities.
/// Implements IModuleManifest from Platform.Core.
/// Runtime manifest only (hash/signature are Stage 7).
/// Architecture reference: §38 (Module Manifest).
/// </summary>
public sealed class InventoryModuleManifest : IModuleManifest
{
    public static readonly InventoryModuleManifest Instance = new();

    private InventoryModuleManifest() { }

    public ModuleId ModuleId => new("inventory");
    public string Name => "Inventory";
    public ModuleVersion Version => new(1, 0, 0);
    public string Publisher => "GenericPOS Platform";
    public ModuleVersion MinimumPlatformVersion => new(1, 0, 0);
    public ModuleVersion? MaximumPlatformVersion => null;

    /// <summary>Inventory depends on Catalog (via Catalog.Contracts) at runtime.</summary>
    public IReadOnlyList<ModuleDependency> Dependencies =>
    [
        new ModuleDependency(
            new ModuleId("catalog"),
            VersionRange.AtLeast(new ModuleVersion(1, 0, 0)))
    ];

    public IReadOnlyList<IFeatureDescriptor> ProvidedFeatures =>
    [
        new InventoryFeatureDescriptor(new FeatureId("inventory.warehouses"), "Warehouse Management"),
        new InventoryFeatureDescriptor(new FeatureId("inventory.stock"), "Stock Management"),
        new InventoryFeatureDescriptor(new FeatureId("inventory.adjustments"), "Stock Adjustments"),
        new InventoryFeatureDescriptor(new FeatureId("inventory.movements"), "Stock Movement History")
    ];

    public int DatabaseSchemaVersion => 1;

    private sealed record InventoryFeatureDescriptor(FeatureId Id, string DisplayName) : IFeatureDescriptor;
}
