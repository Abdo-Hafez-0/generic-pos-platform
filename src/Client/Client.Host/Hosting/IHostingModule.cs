using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Client.Host.Hosting;

/// <summary>
/// Represents a unit of service registration that participates in host construction.
///
/// Client.ModuleHost implements this to register its module discovery services.
/// Future business modules (Stage 5+) will implement this to register their own services.
///
/// IMPORTANT:
/// - IHostingModule is NOT the same as IModule (which is Stage 4).
/// - IHostingModule is only responsible for registering services in the DI container.
/// - IModule (Stage 4) is responsible for the full business module lifecycle.
/// - Do not conflate these two concerns.
///
/// This is intentionally minimal in Stage 2. Stage 4 will define the full module contract.
/// </summary>
public interface IHostingModule
{
    /// <summary>
    /// Registers services into the DI container during host construction.
    /// Called once during application startup, before the host is built.
    /// </summary>
    void RegisterServices(HostBuilderContext context, IServiceCollection services);
}
