using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Client.Host.Hosting;
using Inventory.Infrastructure.DependencyInjection;

namespace Inventory.Infrastructure.Module;

/// <summary>
/// The Inventory module's IHostingModule implementation.
///
/// IMPORTANT — Separation of concerns (Stage 4 Decision 1):
///   IHostingModule = DI registration at host-build time (this class)
///   IModule        = runtime lifecycle (InventoryModule)
///
/// This class is registered with ApplicationHostBuilder in the application's startup
/// (Client.Desktop App.xaml.cs). It calls AddInventoryModule() to wire all Inventory
/// services into the DI container before the host is built.
///
/// It does NOT manage module lifecycle (Initialize/Start/Stop).
/// </summary>
public sealed class InventoryHostingModule : IHostingModule
{
    public void RegisterServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddInventoryModule(context.Configuration);
    }
}
