using Platform.Core.Modules;

namespace Pricing.Infrastructure.Module;

/// <summary>
/// The Pricing module's runtime manifest (identity, version, hard dependencies, features).
/// Runtime manifest only: package hashes/signatures belong to the update system (Updates.Contracts), not here.
/// 
/// </summary>
public sealed class PricingModuleManifest : IModuleManifest
{
    public static readonly PricingModuleManifest Instance = new();

    private PricingModuleManifest() { }

    public ModuleId ModuleId => new("pricing");
    public string Name => "Pricing";
    public ModuleVersion Version => new(1, 0, 0);
    public string Publisher => "GenericPOS Platform";
    public ModuleVersion MinimumPlatformVersion => new(1, 0, 0);
    public ModuleVersion? MaximumPlatformVersion => null;

    public IReadOnlyList<ModuleDependency> Dependencies =>
    [
        new ModuleDependency(new ModuleId("catalog"), VersionRange.AtLeast(new ModuleVersion(1, 0, 0)))
    ];

    public IReadOnlyList<IFeatureDescriptor> ProvidedFeatures =>
    [
        new PricingFeatureDescriptor(new FeatureId("pricing.lists"), "Price Lists"),
        new PricingFeatureDescriptor(new FeatureId("pricing.resolution"), "Price Resolution")
    ];

    public int DatabaseSchemaVersion => 1;

    private sealed record PricingFeatureDescriptor(FeatureId Id, string DisplayName) : IFeatureDescriptor;
}
