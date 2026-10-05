using Client.Host.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Platform.Application.Abstractions.Security;

namespace Client.Security;

/// <summary>
/// Registers the operating-system data protection for the desktop composition root. On Windows that is DPAPI; elsewhere nothing is
/// registered, and consumers (licensing) fall back to unprotected files and say so - they never invent their own cipher.
/// </summary>
public sealed class ClientSecurityHostingModule : IHostingModule
{
    public void RegisterServices(HostBuilderContext context, IServiceCollection services)
    {
        if (OperatingSystem.IsWindows())
            services.TryAddSingleton<ISecretProtector>(new DpapiSecretProtector());
    }
}

public sealed class ClientSecurityAssemblyMarker;
