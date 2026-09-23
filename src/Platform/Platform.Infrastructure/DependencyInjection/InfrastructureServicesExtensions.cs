using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Application.Abstractions.Data;
using Platform.Infrastructure.Persistence;

namespace Platform.Infrastructure.DependencyInjection;

/// <summary>
/// DI registration extension methods for Platform.Infrastructure services.
///
/// Called from Client.Host's PlatformServicesExtensions.AddPlatformServices().
/// This keeps infrastructure service registration inside Platform.Infrastructure
/// and out of Client.Host (which must not reference infrastructure implementations directly).
///
/// DEPENDENCY DIRECTION:
///   Client.Host.PlatformServicesExtensions
///       calls →
///   Platform.Infrastructure.InfrastructureServicesExtensions
///       registers →
///   Platform.Infrastructure implementations
///
/// Client.Host does NOT reference Platform.Infrastructure types directly.
/// It calls this extension via an assembly-scanning pattern or via a direct
/// reference that is justified because Client.Host is the composition root.
///
/// NOTE ON CLIENT.HOST REFERENCE:
/// Client.Host already references Platform.Application but NOT Platform.Infrastructure.
/// To avoid adding Platform.Infrastructure as a compile-time reference to Client.Host,
/// this method is called from within Platform.Infrastructure itself via a startup
/// contribution pattern. The GenericApplicationHost triggers it by calling
/// PlatformServicesExtensions which routes to this extension.
/// For Stage 3, the simplest correct approach is to add Platform.Infrastructure
/// as a reference to Client.Host (the composition root), which is architecturally
/// acceptable because Client.Host IS the composition root.
/// The composition root is allowed to know about all implementations.
/// </summary>
public static class InfrastructureServicesExtensions
{
    /// <summary>
    /// Registers all Platform.Infrastructure services into the DI container.
    ///
    /// Services registered:
    /// - PlatformDbContext (Scoped, SQLite-backed)
    /// - IUnitOfWork → PlatformUnitOfWork (Scoped)
    /// - DatabaseInitializer (Scoped, for startup initialization)
    ///
    /// EF Core lifetime note:
    /// DbContext is Scoped. In a desktop application without HTTP request scope,
    /// "Scoped" means the lifetime of an explicit IServiceScope created per operation.
    /// The PlatformUnitOfWork manages the scope boundary for persistence operations.
    /// </summary>
    public static IServiceCollection AddPlatformInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Bind and validate database options.
        var dbOptions = new DatabaseOptions();
        configuration.GetSection(DatabaseOptions.SectionName).Bind(dbOptions);

        var connectionString = dbOptions.BuildConnectionString();

        // Register PlatformDbContext with SQLite provider.
        services.AddDbContext<PlatformDbContext>(options =>
        {
            options.UseSqlite(connectionString, sqliteOptions =>
            {
                // Migration assembly: Platform.Infrastructure owns platform migrations.
                // When module DbContexts are introduced (Stage 5+), each module sets
                // its own migration assembly in its own AddDbContext call.
                sqliteOptions.MigrationsAssembly(typeof(PlatformDbContext).Assembly.FullName);
            });

            // Enable sensitive data logging only in development.
            // This is gated by configuration so it's never on in production.
#if DEBUG
            options.EnableSensitiveDataLogging();
            options.EnableDetailedErrors();
#endif
        });

        // Register IUnitOfWork -> PlatformUnitOfWork (Scoped).
        // Business modules will register their own IUnitOfWork implementations
        // in their own service registration methods (Stage 5+).
        services.AddScoped<IUnitOfWork, PlatformUnitOfWork>();
        services.AddScoped<PlatformUnitOfWork>();

        // Register the database initializer.
        // Called explicitly during application startup (not automatically on DI resolution).
        services.AddScoped<DatabaseInitializer>();

        return services;
    }
}
