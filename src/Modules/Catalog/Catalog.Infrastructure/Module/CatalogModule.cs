using Platform.Core.Modules;

namespace Catalog.Infrastructure.Module;

/// <summary>
/// The Catalog module's runtime lifecycle implementation.
///
/// Implements IModule from Platform.Core.
/// Manages: Initialize, Start, Stop lifecycle transitions.
///
/// Note: DI registration is NOT done here — that belongs to CatalogHostingModule (IHostingModule).
/// This separation is per Stage 4 architectural decision (Decision 1).
///
/// Architecture reference: §39 (Module Lifecycle), §40 (Module Interface).
/// </summary>
public sealed class CatalogModule : IModule
{
    private ModuleRuntimeStatus _status = ModuleRuntimeStatus.Registered;

    public IModuleManifest Manifest => CatalogModuleManifest.Instance;

    public ModuleRuntimeStatus Status => _status;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // Catalog has no async initialization requirements.
        // Database migrations are run separately by CatalogDatabaseInitializer (IHostedService).
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
