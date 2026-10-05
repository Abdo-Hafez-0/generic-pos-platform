using Platform.Core.Modules;

namespace Users.Infrastructure.Module;

/// <summary>
/// The Users module's runtime manifest (identity, version, hard dependencies, features).
/// Runtime manifest only: package hashes/signatures belong to the update system (Updates.Contracts), not here.
/// 
/// </summary>
public sealed class UsersModuleManifest : IModuleManifest
{
    public static readonly UsersModuleManifest Instance = new();

    private UsersModuleManifest() { }

    public ModuleId ModuleId => new("users");
    public string Name => "Users";
    public ModuleVersion Version => new(1, 0, 0);
    public string Publisher => "GenericPOS Platform";
    public ModuleVersion MinimumPlatformVersion => new(1, 0, 0);
    public ModuleVersion? MaximumPlatformVersion => null;

    public IReadOnlyList<ModuleDependency> Dependencies =>
    [

    ];

    public IReadOnlyList<IFeatureDescriptor> ProvidedFeatures =>
    [
        new UsersFeatureDescriptor(new FeatureId("users.management"), "User Management"),
        new UsersFeatureDescriptor(new FeatureId("users.roles"), "Roles and Permissions")
    ];

    public int DatabaseSchemaVersion => 1;

    private sealed record UsersFeatureDescriptor(FeatureId Id, string DisplayName) : IFeatureDescriptor;
}
