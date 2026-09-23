using Client.Host.Hosting;
using Client.ModuleHost.Discovery;
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
///
/// Services NOT registered here (deferred to their stages):
///   - IModule loading/activation and assembly loading (Stage 4 deferred to runtime host)
///   - License entitlement checking                   (Stage 6)
///   - Assembly signature validation                  (Stage 7)
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
    }
}
