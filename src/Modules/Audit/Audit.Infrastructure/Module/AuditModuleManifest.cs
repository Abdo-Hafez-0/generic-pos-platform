using Platform.Core.Modules;

namespace Audit.Infrastructure.Module;

/// <summary>
/// The Audit module's runtime manifest (identity, version, hard dependencies, features).
/// Runtime manifest only: package hashes/signatures belong to the update system (Updates.Contracts), not here.
/// 
/// </summary>
public sealed class AuditModuleManifest : IModuleManifest
{
    public static readonly AuditModuleManifest Instance = new();

    private AuditModuleManifest() { }

    public ModuleId ModuleId => new("audit");
    public string Name => "Audit";
    public ModuleVersion Version => new(1, 0, 0);
    public string Publisher => "GenericPOS Platform";
    public ModuleVersion MinimumPlatformVersion => new(1, 0, 0);
    public ModuleVersion? MaximumPlatformVersion => null;

    public IReadOnlyList<ModuleDependency> Dependencies =>
    [

    ];

    public IReadOnlyList<IFeatureDescriptor> ProvidedFeatures =>
    [
        new AuditFeatureDescriptor(new FeatureId("audit.log"), "Audit Log")
    ];

    public int DatabaseSchemaVersion => 1;

    private sealed record AuditFeatureDescriptor(FeatureId Id, string DisplayName) : IFeatureDescriptor;
}
