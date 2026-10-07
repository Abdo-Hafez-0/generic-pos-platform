using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Catalog.Contracts.Interfaces;
using Inventory.Contracts.Interfaces;
using Sales.Application.Abstractions;
using Sales.Application.Commands;
using Sales.Application.Queries;
using Sales.Application.Repositories;
using Sales.Contracts.Interfaces;
using Sales.Infrastructure.Module;
using Sales.Infrastructure.Persistence;
using Sales.Infrastructure.Repositories;
using Sales.Infrastructure.Services;
using Platform.Core.Modules;
using Platform.Infrastructure.Persistence;

namespace Sales.Infrastructure.DependencyInjection;

/// <summary>
/// DI registration extension for Sales module services.
///
/// Called by SalesHostingModule.RegisterServices().
/// All Sales DI registration stays inside Sales.Infrastructure.
///
/// Registered services:
/// - SalesDbContext (Scoped, SQLite — same file as other module DbContexts)
/// - ISalesUnitOfWork -> SalesUnitOfWork (Scoped)
/// - ISaleRepository -> EfSaleRepository (Scoped)
/// - IReturnRepository -> EfReturnRepository (Scoped)
/// - ISalesTransactionRepository -> EfSalesTransactionRepository (Scoped)
/// - ISalesReader -> SalesReader (Scoped, implements Sales.Contracts)
/// - ISalesService -> SalesService (Scoped, implements Sales.Contracts)
/// - IModule -> SalesModule (Singleton)
/// - SalesDatabaseInitializer (IHostedService, Singleton)
/// - Command and query handlers (Transient)
/// </summary>
public static class SalesServicesExtensions
{
    public static IServiceCollection AddSalesModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Resolve the same connection string used by PlatformDbContext and other module DbContexts.
        // All module DbContexts share the same physical SQLite file.
        var dbOptions = new DatabaseOptions();
        configuration.GetSection(DatabaseOptions.SectionName).Bind(dbOptions);
        var connectionString = dbOptions.BuildConnectionString();

        // Register SalesDbContext with the same SQLite file.
        // Migration assembly = Sales.Infrastructure (owns Sales migrations).
        services.AddAtomicOperations().AddDbContext<SalesDbContext>((sp, options) =>
        {
            options.UseSharedSqlite(sp, connectionString, sqliteOptions =>
            {
                sqliteOptions.MigrationsAssembly(typeof(SalesDbContext).Assembly.FullName);
            });

#if DEBUG
            options.EnableSensitiveDataLogging();
            options.EnableDetailedErrors();
#endif
        });

        // Unit of Work
        services.AddScoped<ISalesUnitOfWork, SalesUnitOfWork>();

        // Repositories
        services.AddScoped<ISaleRepository, EfSaleRepository>();
        services.AddScoped<IReturnRepository, EfReturnRepository>();
        services.AddScoped<ISalesTransactionRepository, EfSalesTransactionRepository>();

        // Cross-module Contracts (Sales.Contracts implementations)
        services.AddScoped<ISalesReader, SalesReader>();
        services.AddScoped<ISalesService, SalesService>();

        // Module lifecycle (Singleton)
        services.AddSingleton<IModule, SalesModule>();

        // Database migration hosted service (runs during IHost.StartAsync)
        services.AddHostedService<SalesDatabaseInitializer>();

        // Command handlers (Transient — stateless)
        services.AddTransient<CreateSaleCommandHandler>();
        services.AddTransient<AddSaleItemCommandHandler>();
        services.AddTransient<ConfirmSaleCommandHandler>();
        services.AddTransient<CompleteSaleCommandHandler>();
        services.AddTransient<CancelSaleCommandHandler>();

        // Query handlers (Transient — stateless)
        services.AddTransient<GetSaleByIdQueryHandler>();
        services.AddTransient<GetAllSalesQueryHandler>();
        services.AddTransient<GetSalesHistoryQueryHandler>();

        return services;
    }
}
