using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Catalog.Application.Abstractions;
using Catalog.Application.Commands;
using Catalog.Application.Queries;
using Catalog.Application.Repositories;
using Catalog.Contracts.Interfaces;
using Catalog.Infrastructure.Module;
using Catalog.Infrastructure.Persistence;
using Catalog.Infrastructure.Repositories;
using Catalog.Infrastructure.Services;
using Platform.Core.Modules;
using Platform.Infrastructure.Persistence;

namespace Catalog.Infrastructure.DependencyInjection;

/// <summary>
/// DI registration extension for Catalog module services.
///
/// Called by CatalogHostingModule.RegisterServices().
/// This keeps all Catalog DI registration inside Catalog.Infrastructure —
/// the composition root (Client.Host) does not need to know about individual services.
///
/// Registered services:
/// - CatalogDbContext (Scoped, SQLite — same file as PlatformDbContext)
/// - ICatalogUnitOfWork -> CatalogUnitOfWork (Scoped)
/// - IProductRepository -> EfProductRepository (Scoped)
/// - ICategoryRepository -> EfCategoryRepository (Scoped)
/// - IUnitRepository -> EfUnitRepository (Scoped)
/// - IBarcodeRepository -> EfBarcodeRepository (Scoped)
/// - IProductLookup -> CatalogProductLookup (Scoped)
/// - IProductBarcodeResolver -> CatalogBarcodeResolver (Scoped)
/// - IModule -> CatalogModule (Singleton)
/// - CatalogDatabaseInitializer (IHostedService, Singleton)
/// - Command and query handlers (Transient)
/// </summary>
public static class CatalogServicesExtensions
{
    public static IServiceCollection AddCatalogModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Resolve the same connection string used by PlatformDbContext.
        // All module DbContexts share the same physical SQLite file.
        var dbOptions = new DatabaseOptions();
        configuration.GetSection(DatabaseOptions.SectionName).Bind(dbOptions);
        var connectionString = dbOptions.BuildConnectionString();

        // Register CatalogDbContext with the same SQLite file.
        // Migration assembly = Catalog.Infrastructure (owns Catalog migrations).
        services.AddAtomicOperations().AddDbContext<CatalogDbContext>((sp, options) =>
        {
            options.UseSharedSqlite(sp, connectionString, sqliteOptions =>
            {
                sqliteOptions.MigrationsAssembly(typeof(CatalogDbContext).Assembly.FullName);
            });

#if DEBUG
            options.EnableSensitiveDataLogging();
            options.EnableDetailedErrors();
#endif
        });

        // Unit of Work
        services.AddScoped<ICatalogUnitOfWork, CatalogUnitOfWork>();

        // Repositories
        services.AddScoped<IProductRepository, EfProductRepository>();
        services.AddScoped<ICategoryRepository, EfCategoryRepository>();
        services.AddScoped<IUnitRepository, EfUnitRepository>();
        services.AddScoped<IBarcodeRepository, EfBarcodeRepository>();

        // Cross-module Contracts (Catalog.Contracts implementations)
        services.AddScoped<IProductLookup, CatalogProductLookup>();
        services.AddScoped<IProductBarcodeResolver, CatalogBarcodeResolver>();

        // Module lifecycle (Singleton — IModule is stateful across the application lifetime)
        services.AddSingleton<IModule, CatalogModule>();

        // Database migration hosted service (runs during IHost.StartAsync)
        services.AddHostedService<CatalogDatabaseInitializer>();

        // Command handlers (Transient — stateless)
        services.AddSingleton<Platform.Application.Abstractions.Authorization.ICapabilityProvider, Catalog.Application.Security.CatalogCapabilityProvider>();
        services.AddTransient<CreateProductCommandHandler>();
        services.AddTransient<UpdateProductCommandHandler>();
        services.AddTransient<DeactivateProductCommandHandler>();
        services.AddTransient<CreateCategoryCommandHandler>();
        services.AddTransient<CreateUnitCommandHandler>();
        services.AddTransient<AssignBarcodeCommandHandler>();

        // Query handlers (Transient — stateless)
        services.AddTransient<GetProductByIdQueryHandler>();
        services.AddTransient<GetProductBySkuQueryHandler>();
        services.AddTransient<FindProductByBarcodeQueryHandler>();
        services.AddTransient<GetAllCategoriesQueryHandler>();
        services.AddTransient<GetAllUnitsQueryHandler>();

        return services;
    }
}
