using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Core.Modules;
using Platform.Infrastructure.Persistence;
using Audit.Infrastructure.Module;
using Audit.Infrastructure.Persistence;

namespace Audit.Infrastructure.DependencyInjection;

/// <summary>DI registration for the Audit module (called by AuditHostingModule).</summary>
public static class AuditServicesExtensions
{
    public static IServiceCollection AddAuditModule(this IServiceCollection services, IConfiguration configuration)
    {
        // The same physical SQLite file as every other module DbContext.
        var dbOptions = new DatabaseOptions();
        configuration.GetSection(DatabaseOptions.SectionName).Bind(dbOptions);
        var connectionString = dbOptions.BuildConnectionString();

        services.AddDbContext<AuditDbContext>(options =>
        {
            options.UseSqlite(connectionString, sqliteOptions =>
            {
                sqliteOptions.MigrationsAssembly(typeof(AuditDbContext).Assembly.FullName);
            });

#if DEBUG
            options.EnableSensitiveDataLogging();
            options.EnableDetailedErrors();
#endif
        });

        services.AddAuditCore();

        // Security events (sign-ins, denials, license and update verdicts...) are written to the audit log through this listener.
        services.AddSingleton<Audit.Infrastructure.Services.AuditSecurityEventListener>();
        services.AddSingleton<Platform.Application.Abstractions.Security.ISecurityEventListener>(
            sp => sp.GetRequiredService<Audit.Infrastructure.Services.AuditSecurityEventListener>());

        services.AddSingleton<IModule, AuditModule>();
        services.AddHostedService<AuditDatabaseInitializer>();
        return services;
    }

    /// <summary>The module's services without the DbContext/hosting plumbing (also used by tests with an in-memory database).</summary>
    public static IServiceCollection AddAuditCore(this IServiceCollection services)
    {
        services.AddScoped<Audit.Application.Abstractions.IAuditUnitOfWork, Audit.Infrastructure.Persistence.AuditUnitOfWork>();
        services.AddScoped<Audit.Application.Repositories.IAuditEntryRepository, Audit.Infrastructure.Repositories.EfAuditEntryRepository>();
        services.AddScoped<Audit.Contracts.Interfaces.IAuditRecorder, Audit.Infrastructure.Services.AuditRecorder>();
        services.AddScoped<Audit.Contracts.Interfaces.IAuditReader, Audit.Infrastructure.Services.AuditReader>();
        services.AddSingleton<Platform.Application.Abstractions.Authorization.ICapabilityProvider, Audit.Application.Security.AuditCapabilityProvider>();
        services.AddTransient<Audit.Application.Commands.RecordAuditEntryCommandHandler>();
        services.AddTransient<Audit.Application.Queries.GetAuditEntryQueryHandler>();
        services.AddTransient<Audit.Application.Queries.QueryAuditEntriesQueryHandler>();
        return services;
    }
}
