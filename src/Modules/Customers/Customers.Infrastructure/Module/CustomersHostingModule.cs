using Client.Host.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Customers.Infrastructure.DependencyInjection;

namespace Customers.Infrastructure.Module;

/// <summary>
/// The Customers module's IHostingModule: wires all Customers services into the DI container at host-build time.
/// (IHostingModule = DI registration; IModule = runtime lifecycle - kept separate by design.)
/// </summary>
public sealed class CustomersHostingModule : IHostingModule
{
    public void RegisterServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddCustomersModule(context.Configuration);
    }
}
