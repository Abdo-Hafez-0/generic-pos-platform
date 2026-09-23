using Platform.Core.Modules;

namespace Platform.ModuleContract.Tests.Helpers;

/// <summary>
/// Test implementation of IModuleManifest — allows tests to exercise the dependency
/// resolver and registry without real business module assemblies.
/// </summary>
internal sealed class TestModuleManifest : IModuleManifest
{
    public ModuleId ModuleId { get; }
    public string Name { get; }
    public ModuleVersion Version { get; }
    public string Publisher { get; }
    public ModuleVersion MinimumPlatformVersion { get; }
    public ModuleVersion? MaximumPlatformVersion { get; }
    public IReadOnlyList<ModuleDependency> Dependencies { get; }
    public IReadOnlyList<IFeatureDescriptor> ProvidedFeatures { get; }
    public int DatabaseSchemaVersion { get; }

    public TestModuleManifest(
        string moduleId,
        string version = "1.0.0",
        IReadOnlyList<ModuleDependency>? dependencies = null,
        IReadOnlyList<IFeatureDescriptor>? features = null,
        string name = "",
        string publisher = "TestPublisher",
        string minimumPlatformVersion = "1.0.0",
        int databaseSchemaVersion = 0)
    {
        ModuleId = new ModuleId(moduleId);
        Version = ModuleVersion.Parse(version);
        Name = string.IsNullOrEmpty(name) ? moduleId : name;
        Publisher = publisher;
        MinimumPlatformVersion = ModuleVersion.Parse(minimumPlatformVersion);
        MaximumPlatformVersion = null;
        Dependencies = dependencies ?? [];
        ProvidedFeatures = features ?? [];
        DatabaseSchemaVersion = databaseSchemaVersion;
    }
}

/// <summary>
/// Test implementation of IFeatureDescriptor.
/// </summary>
internal sealed class TestFeatureDescriptor : IFeatureDescriptor
{
    public FeatureId Id { get; }
    public string DisplayName { get; }

    public TestFeatureDescriptor(string featureId, string displayName = "")
    {
        Id = new FeatureId(featureId);
        DisplayName = string.IsNullOrEmpty(displayName) ? featureId : displayName;
    }
}

/// <summary>
/// Test implementation of IModule — allows tests to exercise the registry
/// without real business module assemblies.
/// </summary>
internal sealed class TestModule : IModule
{
    public IModuleManifest Manifest { get; }
    public ModuleRuntimeStatus Status { get; private set; } = ModuleRuntimeStatus.Registered;

    public TestModule(IModuleManifest manifest)
    {
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        Status = ModuleRuntimeStatus.Running;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Status = ModuleRuntimeStatus.Stopped;
        return Task.CompletedTask;
    }
}
