using Platform.Core.Modules;

namespace Payments.Infrastructure.Module;

/// <summary>The Payments module's runtime lifecycle (IModule). DI registration lives in PaymentsHostingModule (IHostingModule).</summary>
public sealed class PaymentsModule : IModule
{
    private ModuleRuntimeStatus _status = ModuleRuntimeStatus.Registered;

    public IModuleManifest Manifest => PaymentsModuleManifest.Instance;

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
