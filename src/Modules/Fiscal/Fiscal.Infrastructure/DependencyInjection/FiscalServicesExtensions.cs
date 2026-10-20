using Fiscal.Infrastructure.Module;
using Fiscal.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Core.Modules;
using Platform.Infrastructure.Persistence;

namespace Fiscal.Infrastructure.DependencyInjection;

/// <summary>DI registration for the Fiscal module (called by FiscalHostingModule).</summary>
public static class FiscalServicesExtensions
{
    public static IServiceCollection AddFiscalModule(this IServiceCollection services, IConfiguration configuration)
    {
        // The same physical SQLite file as every other module DbContext.
        var dbOptions = new DatabaseOptions();
        configuration.GetSection(DatabaseOptions.SectionName).Bind(dbOptions);
        var connectionString = dbOptions.BuildConnectionString();

        services.AddAtomicOperations().AddDbContext<FiscalDbContext>((sp, options) =>
        {
            options.UseSharedSqlite(sp, connectionString, sqliteOptions =>
            {
                sqliteOptions.MigrationsAssembly(typeof(FiscalDbContext).Assembly.FullName);
            });

#if DEBUG
            options.EnableSensitiveDataLogging();
            options.EnableDetailedErrors();
#endif
        });

        services.AddFiscalCore();

        // MISS-11a-3: the warning limits and the background sender
        var warnings = new Fiscal.Application.Queries.FiscalWarningOptions();
        configuration.GetSection(Fiscal.Application.Queries.FiscalWarningOptions.SectionName).Bind(warnings);
        services.AddSingleton(warnings);
        var sender = new Fiscal.Infrastructure.Services.FiscalSenderOptions();
        configuration.GetSection(Fiscal.Infrastructure.Services.FiscalSenderOptions.SectionName).Bind(sender);
        services.AddSingleton(sender);

        services.AddSingleton<IModule, FiscalModule>();
        services.AddHostedService<FiscalDatabaseInitializer>();
        services.AddHostedService<Fiscal.Infrastructure.Services.FiscalOutboxService>();
        return services;
    }

    /// <summary>The module's services without the DbContext/hosting plumbing (also used by tests with an in-memory database).</summary>
    public static IServiceCollection AddFiscalCore(this IServiceCollection services)
    {
        services.AddScoped<Fiscal.Application.Abstractions.IFiscalUnitOfWork, FiscalUnitOfWork>();
        services.AddScoped<Fiscal.Application.Repositories.IFiscalSetupRepository, Fiscal.Infrastructure.Repositories.EfFiscalSetupRepository>();
        services.AddScoped<Fiscal.Contracts.Interfaces.IFiscalSetupReader, Fiscal.Infrastructure.Repositories.FiscalSetupReader>();
        services.AddScoped<Fiscal.Application.Repositories.IFiscalDocumentRepository, Fiscal.Infrastructure.Repositories.EfFiscalDocumentRepository>();
        services.AddScoped<Fiscal.Contracts.Interfaces.IFiscalRecorder, Fiscal.Infrastructure.Repositories.FiscalRecorder>();   // MISS-11a-2
        services.AddTransient<Fiscal.Application.Commands.RecordFiscalDocumentCommandHandler>();
        services.AddTransient<Fiscal.Application.Commands.SendFiscalDocumentsCommandHandler>();          // MISS-11a-3
        services.AddTransient<Fiscal.Application.Queries.ListFiscalDocumentsQueryHandler>();
        services.AddTransient<Fiscal.Application.Queries.FiscalAlertsReader>();
        services.AddTransient<Fiscal.Application.Queries.GetFiscalAlertsQueryHandler>();
        services.AddScoped<Fiscal.Contracts.Interfaces.IFiscalNotices, Fiscal.Infrastructure.Repositories.FiscalNotices>();
        services.AddSingleton<Platform.Application.Abstractions.Authorization.ICapabilityProvider, Fiscal.Application.Security.FiscalCapabilityProvider>();
        services.AddTransient<Fiscal.Application.Queries.GetFiscalSetupQueryHandler>();
        services.AddTransient<Fiscal.Application.Commands.SaveFiscalSettingsCommandHandler>();
        return services;
    }
}
