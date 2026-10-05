using Client.Host.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using CashManagement.Infrastructure.DependencyInjection;

namespace CashManagement.Infrastructure.Module;

/// <summary>
/// The CashManagement module's IHostingModule: wires all CashManagement services into the DI container at host-build time.
/// (IHostingModule = DI registration; IModule = runtime lifecycle - kept separate by design.)
/// </summary>
public sealed class CashManagementHostingModule : IHostingModule
{
    public void RegisterServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddCashManagementModule(context.Configuration);
    }
}
