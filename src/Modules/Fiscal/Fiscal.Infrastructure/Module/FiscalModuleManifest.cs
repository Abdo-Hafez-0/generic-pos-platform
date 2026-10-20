using Platform.Core.Modules;

namespace Fiscal.Infrastructure.Module;

/// <summary>
/// The Fiscal module's runtime manifest (identity, version, hard dependencies, features).
/// Runtime manifest only: package hashes/signatures belong to the update system (Updates.Contracts), not here.
/// 
/// </summary>
public sealed class FiscalModuleManifest : IModuleManifest
{
    public static readonly FiscalModuleManifest Instance = new();

    private FiscalModuleManifest() { }

    public ModuleId ModuleId => new("fiscal");
    public string Name => "Fiscal";
    public ModuleVersion Version => new(1, 0, 0);
    public string Publisher => "GenericPOS Platform";
    public ModuleVersion MinimumPlatformVersion => new(1, 0, 0);
    public ModuleVersion? MaximumPlatformVersion => null;

    public IReadOnlyList<ModuleDependency> Dependencies =>
    [

    ];

    public IReadOnlyList<IFeatureDescriptor> ProvidedFeatures =>
    [
        new FiscalFeatureDescriptor(new FeatureId("fiscal.receipts"), "Electronic receipts for the tax authority")
    ];

    public int DatabaseSchemaVersion => 1;

    private sealed record FiscalFeatureDescriptor(FeatureId Id, string DisplayName) : IFeatureDescriptor;
}
