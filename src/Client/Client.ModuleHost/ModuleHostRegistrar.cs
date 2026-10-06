using Client.Host.Hosting;
using Client.ModuleHost.Discovery;
using Client.ModuleHost.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Application.Modules;

namespace Client.ModuleHost;

/// <summary>
/// Registers all Client.ModuleHost services into the application DI container.
///
/// Usage in App.xaml.cs:
///   ApplicationHostBuilder
///       .Create()
///       .WithModule(new ModuleHostRegistrar())
///       .Build();
///
/// Services registered:
///
///   IModuleDiscoveryService      → FileSystemModuleDiscoveryService   (Singleton)
///   IModuleRegistry              → ModuleRegistry                     (Singleton)
///   IModuleDependencyResolver    → ModuleDependencyResolver           (Singleton)
///   ModuleLifecycleService       (hosted) validate → initialize → register → start the composed modules, stop them on shutdown (Stage 13)
///
/// Not done here: loading module assemblies from disk (the composed modules are compiled in; adopting updater-deployed versions needs
/// a launcher), license enforcement (Platform.Application AuthorizationService), package signature validation (Client.Updater).
///
/// Note on IHostingModule vs IModule:
///   This registrar implements IHostingModule — it handles DI wiring at host-build time.
///   IModule is the business module runtime contract defined in Platform.Core.
///   These two interfaces serve different purposes and intentionally remain separate.
/// </summary>
public sealed class ModuleHostRegistrar : IHostingModule
{
    /// <inheritdoc />
    public void RegisterServices(HostBuilderContext context, IServiceCollection services)
    {
        // Discovery: scans the file system for module assembly candidates.
        services.AddSingleton<IModuleDiscoveryService, FileSystemModuleDiscoveryService>();

        // Registry: platform runtime catalog of registered IModule instances.
        services.AddSingleton<IModuleRegistry, ModuleRegistry>();

        // Dependency resolver: validates dependency graphs and produces activation order.
        services.AddSingleton<IModuleDependencyResolver, ModuleDependencyResolver>();

        // Lifecycle: runs after every hosted service (databases are ready), fails the start on an invalid composition.
        services.AddHostedService<ModuleLifecycleService>();
    }
}
