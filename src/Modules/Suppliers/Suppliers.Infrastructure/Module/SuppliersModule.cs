using Platform.Core.Modules;

namespace Suppliers.Infrastructure.Module;

/// <summary>The Suppliers module's runtime lifecycle (IModule). DI registration lives in SuppliersHostingModule (IHostingModule).</summary>
public sealed class SuppliersModule : IModule
{
    private ModuleRuntimeStatus _status = ModuleRuntimeStatus.Registered;

    public IModuleManifest Manifest => SuppliersModuleManifest.Instance;

    public ModuleRuntimeStatus Status => _status;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _status = ModuleRuntimeStatus.Enabled;
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _status = ModuleRuntimeStatus.Running;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _status = ModuleRuntimeStatus.Stopped;
        return Task.CompletedTask;
    }
}
