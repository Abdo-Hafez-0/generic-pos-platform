using Platform.Core.Modules;

namespace Payments.Infrastructure.Module;

/// <summary>
/// The Payments module's runtime manifest (identity, version, hard dependencies, features).
/// Runtime manifest only: package hashes/signatures belong to the update system (Updates.Contracts), not here.
/// 
/// </summary>
public sealed class PaymentsModuleManifest : IModuleManifest
{
    public static readonly PaymentsModuleManifest Instance = new();

    private PaymentsModuleManifest() { }

    public ModuleId ModuleId => new("payments");
    public string Name => "Payments";
    public ModuleVersion Version => new(1, 0, 0);
    public string Publisher => "GenericPOS Platform";
    public ModuleVersion MinimumPlatformVersion => new(1, 0, 0);
    public ModuleVersion? MaximumPlatformVersion => null;

    public IReadOnlyList<ModuleDependency> Dependencies =>
    [

    ];

    public IReadOnlyList<IFeatureDescriptor> ProvidedFeatures =>
    [
        new PaymentsFeatureDescriptor(new FeatureId("payments.recording"), "Payment Recording")
    ];

    public int DatabaseSchemaVersion => 1;

    private sealed record PaymentsFeatureDescriptor(FeatureId Id, string DisplayName) : IFeatureDescriptor;
}
