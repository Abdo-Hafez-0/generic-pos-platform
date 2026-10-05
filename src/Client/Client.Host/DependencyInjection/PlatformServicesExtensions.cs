using Client.Host.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Infrastructure.DependencyInjection;

namespace Client.Host.DependencyInjection;

/// <summary>
/// DI registration extension methods for platform-level services.
///
/// This is the composition root entry point for Platform services.
/// It delegates to Platform.Infrastructure for infrastructure registrations.
///
/// COMPOSITION ROOT JUSTIFICATION:
/// Client.Host IS the application composition root. It is architecturally correct
/// and accepted practice for the composition root to reference implementation assemblies.
/// Client.Host references Platform.Infrastructure ONLY for the purpose of calling
/// InfrastructureServicesExtensions.AddPlatformInfrastructure().
/// No Platform.Infrastructure types are used directly anywhere except DI registration here.
///
/// Registrations by stage:
/// - Stage 2: (nothing — Platform.Infrastructure was empty)
/// - Stage 3: IUnitOfWork, PlatformDbContext, DatabaseInitializer (via AddPlatformInfrastructure)
/// - Stage 4: IDomainEventPublisher implementation
///
/// Do not add business module services here.
/// Each module registers its own services via IHostingModule.RegisterServices().
/// </summary>
public static class PlatformServicesExtensions
{
    /// <summary>
    /// Registers all platform-level services into the DI container.
    /// </summary>
    public static IServiceCollection AddPlatformServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Stage 3: Register Platform.Infrastructure services (DbContext, UoW, initializer).
        services.AddPlatformInfrastructure(configuration);

        // Stage 11: session, capability catalog, authorization and security events (module-neutral).
        services.AddPlatformSecurity();

        // Register the database initialization hosted service.
        // This runs during IHost.StartAsync() before the WPF window is shown.
        services.AddHostedService<DatabaseInitializerService>();

        return services;
    }
}
