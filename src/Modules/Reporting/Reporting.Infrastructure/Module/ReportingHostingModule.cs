using Client.Host.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Reporting.Infrastructure.DependencyInjection;

namespace Reporting.Infrastructure.Module;

/// <summary>
/// The Reporting module's IHostingModule: wires all Reporting services into the DI container at host-build time.
/// (IHostingModule = DI registration; IModule = runtime lifecycle - kept separate by design.)
/// </summary>
public sealed class ReportingHostingModule : IHostingModule
{
    public void RegisterServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddReportingModule(context.Configuration);
    }
}
