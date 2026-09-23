using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Client.Host.Hosting;
using Catalog.Infrastructure.DependencyInjection;

namespace Catalog.Infrastructure.Module;

/// <summary>
/// The Catalog module's IHostingModule implementation.
///
/// IMPORTANT — Separation of concerns (Stage 4 Decision 1):
///   IHostingModule = DI registration at host-build time (this class)
///   IModule        = runtime lifecycle (CatalogModule)
///
/// This class is registered with ApplicationHostBuilder in the application's startup
/// (Client.Desktop App.xaml.cs). It calls AddCatalogModule() to wire all Catalog
/// services into the DI container before the host is built.
///
/// It does NOT manage module lifecycle (Initialize/Start/Stop).
/// </summary>
public sealed class CatalogHostingModule : IHostingModule
{
    public void RegisterServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddCatalogModule(context.Configuration);
    }
}
