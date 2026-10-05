using Platform.Core.Modules;

namespace Reporting.Infrastructure.Module;

/// <summary>
/// The Reporting module's runtime manifest (identity, version, hard dependencies, features).
/// Runtime manifest only: package hashes/signatures belong to the update system (Updates.Contracts), not here.
/// 
/// </summary>
public sealed class ReportingModuleManifest : IModuleManifest
{
    public static readonly ReportingModuleManifest Instance = new();

    private ReportingModuleManifest() { }

    public ModuleId ModuleId => new("reporting");
    public string Name => "Reporting";
    public ModuleVersion Version => new(1, 0, 0);
    public string Publisher => "GenericPOS Platform";
    public ModuleVersion MinimumPlatformVersion => new(1, 0, 0);
    public ModuleVersion? MaximumPlatformVersion => null;

    public IReadOnlyList<ModuleDependency> Dependencies =>
    [

    ];

    public IReadOnlyList<IFeatureDescriptor> ProvidedFeatures =>
    [
        new ReportingFeatureDescriptor(new FeatureId("reporting.overview"), "Business Overview")
    ];

    public int DatabaseSchemaVersion => 0;

    private sealed record ReportingFeatureDescriptor(FeatureId Id, string DisplayName) : IFeatureDescriptor;
}
