using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Core.Modules;
using Platform.Infrastructure.Persistence;
using CashManagement.Infrastructure.Module;
using CashManagement.Infrastructure.Persistence;

namespace CashManagement.Infrastructure.DependencyInjection;

/// <summary>DI registration for the CashManagement module (called by CashManagementHostingModule).</summary>
public static class CashManagementServicesExtensions
{
    public static IServiceCollection AddCashManagementModule(this IServiceCollection services, IConfiguration configuration)
    {
        // The same physical SQLite file as every other module DbContext.
        var dbOptions = new DatabaseOptions();
        configuration.GetSection(DatabaseOptions.SectionName).Bind(dbOptions);
        var connectionString = dbOptions.BuildConnectionString();

        services.AddDbContext<CashManagementDbContext>(options =>
        {
            options.UseSqlite(connectionString, sqliteOptions =>
            {
                sqliteOptions.MigrationsAssembly(typeof(CashManagementDbContext).Assembly.FullName);
            });

#if DEBUG
            options.EnableSensitiveDataLogging();
            options.EnableDetailedErrors();
#endif
        });

        services.AddCashManagementCore();

        services.AddSingleton<IModule, CashManagementModule>();
        services.AddHostedService<CashManagementDatabaseInitializer>();
        return services;
    }

    /// <summary>The module's services without the DbContext/hosting plumbing (also used by tests with an in-memory database).</summary>
    public static IServiceCollection AddCashManagementCore(this IServiceCollection services)
    {
        services.AddScoped<CashManagement.Application.Abstractions.ICashManagementUnitOfWork, CashManagement.Infrastructure.Persistence.CashManagementUnitOfWork>();
        services.AddScoped<CashManagement.Application.Repositories.ICashSessionRepository, CashManagement.Infrastructure.Repositories.EfCashSessionRepository>();
        services.AddScoped<CashManagement.Contracts.Interfaces.ICashMovementRecorder, CashManagement.Infrastructure.Services.CashMovementRecorder>();
        services.AddScoped<CashManagement.Contracts.Interfaces.ICashSessionReader, CashManagement.Infrastructure.Services.CashSessionReader>();
        services.AddTransient<CashManagement.Application.Commands.OpenCashSessionCommandHandler>();
        services.AddTransient<CashManagement.Application.Commands.RecordCashMovementCommandHandler>();
        services.AddTransient<CashManagement.Application.Commands.CloseCashSessionCommandHandler>();
        services.AddTransient<CashManagement.Application.Queries.GetCashSessionQueryHandler>();
        services.AddTransient<CashManagement.Application.Queries.GetOpenCashSessionQueryHandler>();
        services.AddTransient<CashManagement.Application.Queries.ListCashSessionsQueryHandler>();
        return services;
    }
}
