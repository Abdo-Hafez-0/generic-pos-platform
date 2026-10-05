using Platform.Core.Modules;

namespace Customers.Infrastructure.Module;

/// <summary>The Customers module's runtime lifecycle (IModule). DI registration lives in CustomersHostingModule (IHostingModule).</summary>
public sealed class CustomersModule : IModule
{
    private ModuleRuntimeStatus _status = ModuleRuntimeStatus.Registered;

    public IModuleManifest Manifest => CustomersModuleManifest.Instance;

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
