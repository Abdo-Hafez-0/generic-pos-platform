using Platform.Core.Modules;

namespace Suppliers.Infrastructure.Module;

/// <summary>
/// The Suppliers module's runtime manifest (identity, version, hard dependencies, features).
/// Runtime manifest only: package hashes/signatures belong to the update system (Updates.Contracts), not here.
/// 
/// </summary>
public sealed class SuppliersModuleManifest : IModuleManifest
{
    public static readonly SuppliersModuleManifest Instance = new();

    private SuppliersModuleManifest() { }

    public ModuleId ModuleId => new("suppliers");
    public string Name => "Suppliers";
    public ModuleVersion Version => new(1, 0, 0);
    public string Publisher => "GenericPOS Platform";
    public ModuleVersion MinimumPlatformVersion => new(1, 0, 0);
    public ModuleVersion? MaximumPlatformVersion => null;

    public IReadOnlyList<ModuleDependency> Dependencies =>
    [

    ];

    public IReadOnlyList<IFeatureDescriptor> ProvidedFeatures =>
    [
        new SuppliersFeatureDescriptor(new FeatureId("suppliers.management"), "Supplier Management")
    ];

    public int DatabaseSchemaVersion => 1;

    private sealed record SuppliersFeatureDescriptor(FeatureId Id, string DisplayName) : IFeatureDescriptor;
}
