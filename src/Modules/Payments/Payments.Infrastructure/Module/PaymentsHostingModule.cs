using Client.Host.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Payments.Infrastructure.DependencyInjection;

namespace Payments.Infrastructure.Module;

/// <summary>
/// The Payments module's IHostingModule: wires all Payments services into the DI container at host-build time.
/// (IHostingModule = DI registration; IModule = runtime lifecycle - kept separate by design.)
/// </summary>
public sealed class PaymentsHostingModule : IHostingModule
{
    public void RegisterServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddPaymentsModule(context.Configuration);
    }
}
