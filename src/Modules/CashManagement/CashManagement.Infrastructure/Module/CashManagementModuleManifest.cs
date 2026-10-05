using Platform.Core.Modules;

namespace CashManagement.Infrastructure.Module;

/// <summary>
/// The CashManagement module's runtime manifest (identity, version, hard dependencies, features).
/// Runtime manifest only: package hashes/signatures belong to the update system (Updates.Contracts), not here.
/// 
/// </summary>
public sealed class CashManagementModuleManifest : IModuleManifest
{
    public static readonly CashManagementModuleManifest Instance = new();

    private CashManagementModuleManifest() { }

    public ModuleId ModuleId => new("cash-management");
    public string Name => "CashManagement";
    public ModuleVersion Version => new(1, 0, 0);
    public string Publisher => "GenericPOS Platform";
    public ModuleVersion MinimumPlatformVersion => new(1, 0, 0);
    public ModuleVersion? MaximumPlatformVersion => null;

    public IReadOnlyList<ModuleDependency> Dependencies =>
    [

    ];

    public IReadOnlyList<IFeatureDescriptor> ProvidedFeatures =>
    [
        new CashManagementFeatureDescriptor(new FeatureId("cash.sessions"), "Cash Drawer Sessions")
    ];

    public int DatabaseSchemaVersion => 1;

    private sealed record CashManagementFeatureDescriptor(FeatureId Id, string DisplayName) : IFeatureDescriptor;
}
