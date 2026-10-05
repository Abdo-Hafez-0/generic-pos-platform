using Client.Host.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Suppliers.Infrastructure.DependencyInjection;

namespace Suppliers.Infrastructure.Module;

/// <summary>
/// The Suppliers module's IHostingModule: wires all Suppliers services into the DI container at host-build time.
/// (IHostingModule = DI registration; IModule = runtime lifecycle - kept separate by design.)
/// </summary>
public sealed class SuppliersHostingModule : IHostingModule
{
    public void RegisterServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSuppliersModule(context.Configuration);
    }
}
