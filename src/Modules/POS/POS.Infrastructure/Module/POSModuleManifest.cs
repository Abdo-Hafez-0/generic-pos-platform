using Platform.Core.Modules;

namespace POS.Infrastructure.Module;

/// <summary>
/// The POS module's manifest — describes its identity, version, and capabilities.
/// Implements IModuleManifest from Platform.Core.
/// POS depends on Catalog, Inventory and Sales (via their Contracts) at runtime.
/// Runtime manifest only (hash/signature are Stage 7).
/// </summary>
public sealed class POSModuleManifest : IModuleManifest
{
    public static readonly POSModuleManifest Instance = new();

    private POSModuleManifest() { }

    public ModuleId ModuleId => new("pos");
    public string Name => "POS";
    public ModuleVersion Version => new(1, 0, 0);
    public string Publisher => "GenericPOS Platform";
    public ModuleVersion MinimumPlatformVersion => new(1, 0, 0);
    public ModuleVersion? MaximumPlatformVersion => null;

    public IReadOnlyList<ModuleDependency> Dependencies =>
    [
        new ModuleDependency(new ModuleId("catalog"), VersionRange.AtLeast(new ModuleVersion(1, 0, 0))),
        new ModuleDependency(new ModuleId("inventory"), VersionRange.AtLeast(new ModuleVersion(1, 0, 0))),
        new ModuleDependency(new ModuleId("sales"), VersionRange.AtLeast(new ModuleVersion(1, 0, 0)))
    ];

    public IReadOnlyList<IFeatureDescriptor> ProvidedFeatures =>
    [
        new POSFeatureDescriptor(new FeatureId("pos.sessions"), "POS Sessions"),
        new POSFeatureDescriptor(new FeatureId("pos.checkout"), "POS Checkout")
    ];

    public int DatabaseSchemaVersion => 1;

    private sealed record POSFeatureDescriptor(FeatureId Id, string DisplayName) : IFeatureDescriptor;
}
