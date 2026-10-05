using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Inventory.Application.Abstractions;
using Inventory.Application.Commands;
using Inventory.Application.Queries;
using Inventory.Application.Repositories;
using Inventory.Contracts.Interfaces;
using Inventory.Infrastructure.Module;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Repositories;
using Inventory.Infrastructure.Services;
using Platform.Core.Modules;
using Platform.Infrastructure.Persistence;

namespace Inventory.Infrastructure.DependencyInjection;

/// <summary>
/// DI registration extension for Inventory module services.
///
/// Called by InventoryHostingModule.RegisterServices().
/// All Inventory DI registration stays inside Inventory.Infrastructure.
///
/// Registered services:
/// - InventoryDbContext (Scoped, SQLite — same file as PlatformDbContext and CatalogDbContext)
/// - IInventoryUnitOfWork -> InventoryUnitOfWork (Scoped)
/// - IWarehouseRepository -> EfWarehouseRepository (Scoped)
/// - ILocationRepository -> EfLocationRepository (Scoped)
/// - IStockItemRepository -> EfStockItemRepository (Scoped)
/// - IStockMovementRepository -> EfStockMovementRepository (Scoped)
/// - IStockAdjustmentRepository -> EfStockAdjustmentRepository (Scoped)
/// - IInventoryBalanceRepository -> EfInventoryBalanceRepository (Scoped)
/// - IInventoryReader -> InventoryReader (Scoped)
/// - IStockAvailabilityChecker -> StockAvailabilityChecker (Scoped)
/// - IStockMovementReader -> StockMovementReader (Scoped)
/// - IModule -> InventoryModule (Singleton)
/// - InventoryDatabaseInitializer (IHostedService, Singleton)
/// - Command and query handlers (Transient)
/// </summary>
public static class InventoryServicesExtensions
{
    public static IServiceCollection AddInventoryModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Resolve the same connection string used by PlatformDbContext and CatalogDbContext.
        // All module DbContexts share the same physical SQLite file.
        var dbOptions = new DatabaseOptions();
        configuration.GetSection(DatabaseOptions.SectionName).Bind(dbOptions);
        var connectionString = dbOptions.BuildConnectionString();

        // Register InventoryDbContext with the same SQLite file.
        // Migration assembly = Inventory.Infrastructure (owns Inventory migrations).
        services.AddDbContext<InventoryDbContext>(options =>
        {
            options.UseSqlite(connectionString, sqliteOptions =>
            {
                sqliteOptions.MigrationsAssembly(typeof(InventoryDbContext).Assembly.FullName);
            });

#if DEBUG
            options.EnableSensitiveDataLogging();
            options.EnableDetailedErrors();
#endif
        });

        // Unit of Work
        services.AddScoped<IInventoryUnitOfWork, InventoryUnitOfWork>();

        // Repositories
        services.AddScoped<IWarehouseRepository, EfWarehouseRepository>();
        services.AddScoped<ILocationRepository, EfLocationRepository>();
        services.AddScoped<IStockItemRepository, EfStockItemRepository>();
        services.AddScoped<IStockMovementRepository, EfStockMovementRepository>();
        services.AddScoped<IStockAdjustmentRepository, EfStockAdjustmentRepository>();
        services.AddScoped<IInventoryBalanceRepository, EfInventoryBalanceRepository>();

        // Cross-module Contracts (Inventory.Contracts implementations)
        services.AddScoped<IInventoryReader, InventoryReader>();
        services.AddScoped<IStockAvailabilityChecker, StockAvailabilityChecker>();
        services.AddScoped<IStockMovementReader, StockMovementReader>();
        services.AddScoped<IStockIssueService, StockIssueService>();
        services.AddScoped<IStockReceiptService, StockReceiptService>();

        // Module lifecycle (Singleton)
        services.AddSingleton<IModule, InventoryModule>();

        // Database migration hosted service (runs during IHost.StartAsync)
        services.AddHostedService<InventoryDatabaseInitializer>();

        // Command handlers (Transient — stateless)
        services.AddSingleton<Platform.Application.Abstractions.Authorization.ICapabilityProvider, Inventory.Application.Security.InventoryCapabilityProvider>();
        services.AddTransient<CreateWarehouseCommandHandler>();
        services.AddTransient<CreateLocationCommandHandler>();
        services.AddTransient<AddStockCommandHandler>();
        services.AddTransient<AdjustStockCommandHandler>();
        services.AddTransient<IssueStockCommandHandler>();

        // Query handlers (Transient — stateless)
        services.AddTransient<GetWarehousesQueryHandler>();
        services.AddTransient<GetStockLevelQueryHandler>();
        services.AddTransient<GetAllStockLevelsQueryHandler>();
        services.AddTransient<GetStockMovementsQueryHandler>();

        return services;
    }
}
