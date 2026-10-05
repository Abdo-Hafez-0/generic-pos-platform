using Client.Host.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Users.Infrastructure.DependencyInjection;

namespace Users.Infrastructure.Module;

/// <summary>
/// The Users module's IHostingModule: wires all Users services into the DI container at host-build time.
/// (IHostingModule = DI registration; IModule = runtime lifecycle - kept separate by design.)
/// </summary>
public sealed class UsersHostingModule : IHostingModule
{
    public void RegisterServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddUsersModule(context.Configuration);
    }
}
