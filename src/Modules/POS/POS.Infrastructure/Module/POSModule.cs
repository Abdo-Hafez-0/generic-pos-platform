using Platform.Core.Modules;

namespace POS.Infrastructure.Module;

/// <summary>
/// The POS module's runtime lifecycle implementation.
///
/// Implements IModule from Platform.Core.
/// Note: DI registration is NOT done here — that belongs to POSHostingModule (IHostingModule).
/// This separation is per Stage 4 architectural decision.
///
/// Architecture reference: §39 (Module Lifecycle), §40 (Module Interface).
/// </summary>
public sealed class POSModule : IModule
{
    private ModuleRuntimeStatus _status = ModuleRuntimeStatus.Registered;

    public IModuleManifest Manifest => POSModuleManifest.Instance;

    public ModuleRuntimeStatus Status => _status;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // POS has no async initialization requirements.
        // Database migrations are run separately by POSDatabaseInitializer (IHostedService).
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
