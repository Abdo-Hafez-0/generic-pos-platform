using Platform.Core.Modules;

namespace Sales.Infrastructure.Module;

/// <summary>
/// The Sales module's manifest — describes its identity, version, and capabilities.
/// Implements IModuleManifest from Platform.Core.
/// Runtime manifest only (hash/signature are Stage 7).
/// Architecture reference: §38 (Module Manifest).
/// </summary>
public sealed class SalesModuleManifest : IModuleManifest
{
    public static readonly SalesModuleManifest Instance = new();

    private SalesModuleManifest() { }

    public ModuleId ModuleId => new("sales");
    public string Name => "Sales";
    public ModuleVersion Version => new(1, 0, 0);
    public string Publisher => "GenericPOS Platform";
    public ModuleVersion MinimumPlatformVersion => new(1, 0, 0);
    public ModuleVersion? MaximumPlatformVersion => null;

    /// <summary>Sales depends on Catalog and Inventory (via their Contracts) at runtime.</summary>
    public IReadOnlyList<ModuleDependency> Dependencies =>
    [
        new ModuleDependency(
            new ModuleId("catalog"),
            VersionRange.AtLeast(new ModuleVersion(1, 0, 0))),
        new ModuleDependency(
            new ModuleId("inventory"),
            VersionRange.AtLeast(new ModuleVersion(1, 0, 0)))
    ];

    public IReadOnlyList<IFeatureDescriptor> ProvidedFeatures =>
    [
        new SalesFeatureDescriptor(new FeatureId("sales.transactions"), "Sales Transactions"),
        new SalesFeatureDescriptor(new FeatureId("sales.returns"), "Sales Returns")
    ];

    public int DatabaseSchemaVersion => 1;

    private sealed record SalesFeatureDescriptor(FeatureId Id, string DisplayName) : IFeatureDescriptor;
}
