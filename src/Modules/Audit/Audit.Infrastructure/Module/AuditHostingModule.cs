using Client.Host.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Audit.Infrastructure.DependencyInjection;

namespace Audit.Infrastructure.Module;

/// <summary>
/// The Audit module's IHostingModule: wires all Audit services into the DI container at host-build time.
/// (IHostingModule = DI registration; IModule = runtime lifecycle - kept separate by design.)
/// </summary>
public sealed class AuditHostingModule : IHostingModule
{
    public void RegisterServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddAuditModule(context.Configuration);
    }
}
