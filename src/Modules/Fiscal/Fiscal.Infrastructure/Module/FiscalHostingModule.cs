using Client.Host.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Fiscal.Infrastructure.DependencyInjection;

namespace Fiscal.Infrastructure.Module;

/// <summary>
/// The Fiscal module's IHostingModule: wires all Fiscal services into the DI container at host-build time.
/// (IHostingModule = DI registration; IModule = runtime lifecycle - kept separate by design.)
/// </summary>
public sealed class FiscalHostingModule : IHostingModule
{
    public void RegisterServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddFiscalModule(context.Configuration);
    }
}
