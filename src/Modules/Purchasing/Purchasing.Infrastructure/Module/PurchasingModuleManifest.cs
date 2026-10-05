using Platform.Core.Modules;

namespace Purchasing.Infrastructure.Module;

/// <summary>
/// The Purchasing module's runtime manifest (identity, version, hard dependencies, features).
/// Runtime manifest only: package hashes/signatures belong to the update system (Updates.Contracts), not here.
/// 
/// </summary>
public sealed class PurchasingModuleManifest : IModuleManifest
{
    public static readonly PurchasingModuleManifest Instance = new();

    private PurchasingModuleManifest() { }

    public ModuleId ModuleId => new("purchasing");
    public string Name => "Purchasing";
    public ModuleVersion Version => new(1, 0, 0);
    public string Publisher => "GenericPOS Platform";
    public ModuleVersion MinimumPlatformVersion => new(1, 0, 0);
    public ModuleVersion? MaximumPlatformVersion => null;

    public IReadOnlyList<ModuleDependency> Dependencies =>
    [
        new ModuleDependency(new ModuleId("catalog"), VersionRange.AtLeast(new ModuleVersion(1, 0, 0))),
        new ModuleDependency(new ModuleId("suppliers"), VersionRange.AtLeast(new ModuleVersion(1, 0, 0))),
        new ModuleDependency(new ModuleId("inventory"), VersionRange.AtLeast(new ModuleVersion(1, 0, 0)))
    ];

    public IReadOnlyList<IFeatureDescriptor> ProvidedFeatures =>
    [
        new PurchasingFeatureDescriptor(new FeatureId("purchasing.orders"), "Purchase Orders"),
        new PurchasingFeatureDescriptor(new FeatureId("purchasing.receiving"), "Purchase Receiving")
    ];

    public int DatabaseSchemaVersion => 1;

    private sealed record PurchasingFeatureDescriptor(FeatureId Id, string DisplayName) : IFeatureDescriptor;
}
