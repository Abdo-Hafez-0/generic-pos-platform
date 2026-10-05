using Platform.Core.Modules;

namespace Customers.Infrastructure.Module;

/// <summary>
/// The Customers module's runtime manifest (identity, version, hard dependencies, features).
/// Runtime manifest only: package hashes/signatures belong to the update system (Updates.Contracts), not here.
/// 
/// </summary>
public sealed class CustomersModuleManifest : IModuleManifest
{
    public static readonly CustomersModuleManifest Instance = new();

    private CustomersModuleManifest() { }

    public ModuleId ModuleId => new("customers");
    public string Name => "Customers";
    public ModuleVersion Version => new(1, 0, 0);
    public string Publisher => "GenericPOS Platform";
    public ModuleVersion MinimumPlatformVersion => new(1, 0, 0);
    public ModuleVersion? MaximumPlatformVersion => null;

    public IReadOnlyList<ModuleDependency> Dependencies =>
    [

    ];

    public IReadOnlyList<IFeatureDescriptor> ProvidedFeatures =>
    [
        new CustomersFeatureDescriptor(new FeatureId("customers.management"), "Customer Management")
    ];

    public int DatabaseSchemaVersion => 1;

    private sealed record CustomersFeatureDescriptor(FeatureId Id, string DisplayName) : IFeatureDescriptor;
}
