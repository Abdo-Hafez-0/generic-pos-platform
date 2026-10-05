using Platform.Core.Modules;

namespace CashManagement.Infrastructure.Module;

/// <summary>The CashManagement module's runtime lifecycle (IModule). DI registration lives in CashManagementHostingModule (IHostingModule).</summary>
public sealed class CashManagementModule : IModule
{
    private ModuleRuntimeStatus _status = ModuleRuntimeStatus.Registered;

    public IModuleManifest Manifest => CashManagementModuleManifest.Instance;

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
