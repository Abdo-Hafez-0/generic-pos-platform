using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Client.Host.Hosting;
using POS.Infrastructure.DependencyInjection;

namespace POS.Infrastructure.Module;

/// <summary>
/// The POS module's IHostingModule implementation.
///
/// IMPORTANT — Separation of concerns (Stage 4 Decision 1):
///   IHostingModule = DI registration at host-build time (this class)
///   IModule        = runtime lifecycle (POSModule)
///
/// This class is registered with ApplicationHostBuilder in the application's startup
/// (Client.Desktop App.xaml.cs). It calls AddPOSModule() to wire all POS
/// services into the DI container before the host is built.
///
/// It does NOT manage module lifecycle (Initialize/Start/Stop).
/// </summary>
public sealed class POSHostingModule : IHostingModule
{
    public void RegisterServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddPOSModule(context.Configuration);
    }
}
