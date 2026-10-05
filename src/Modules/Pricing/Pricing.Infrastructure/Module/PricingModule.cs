using Platform.Core.Modules;

namespace Pricing.Infrastructure.Module;

/// <summary>The Pricing module's runtime lifecycle (IModule). DI registration lives in PricingHostingModule (IHostingModule).</summary>
public sealed class PricingModule : IModule
{
    private ModuleRuntimeStatus _status = ModuleRuntimeStatus.Registered;

    public IModuleManifest Manifest => PricingModuleManifest.Instance;

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
