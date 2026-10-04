using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Client.Host.Hosting;
using Sales.Infrastructure.DependencyInjection;

namespace Sales.Infrastructure.Module;

/// <summary>
/// The Sales module's IHostingModule implementation.
///
/// IMPORTANT — Separation of concerns (Stage 4 Decision 1):
///   IHostingModule = DI registration at host-build time (this class)
///   IModule        = runtime lifecycle (SalesModule)
///
/// This class is registered with ApplicationHostBuilder in the application's startup
/// (Client.Desktop App.xaml.cs). It calls AddSalesModule() to wire all Sales
/// services into the DI container before the host is built.
///
/// It does NOT manage module lifecycle (Initialize/Start/Stop).
/// </summary>
public sealed class SalesHostingModule : IHostingModule
{
    public void RegisterServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSalesModule(context.Configuration);
    }
}
