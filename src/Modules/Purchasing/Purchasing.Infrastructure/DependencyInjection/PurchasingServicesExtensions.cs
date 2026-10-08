using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Core.Modules;
using Platform.Infrastructure.Persistence;
using Purchasing.Infrastructure.Module;
using Purchasing.Infrastructure.Persistence;

namespace Purchasing.Infrastructure.DependencyInjection;

/// <summary>DI registration for the Purchasing module (called by PurchasingHostingModule).</summary>
public static class PurchasingServicesExtensions
{
    public static IServiceCollection AddPurchasingModule(this IServiceCollection services, IConfiguration configuration)
    {
        // The same physical SQLite file as every other module DbContext.
        var dbOptions = new DatabaseOptions();
        configuration.GetSection(DatabaseOptions.SectionName).Bind(dbOptions);
        var connectionString = dbOptions.BuildConnectionString();

        services.AddAtomicOperations().AddDbContext<PurchasingDbContext>((sp, options) =>
        {
            options.UseSharedSqlite(sp, connectionString, sqliteOptions =>
            {
                sqliteOptions.MigrationsAssembly(typeof(PurchasingDbContext).Assembly.FullName);
            });

#if DEBUG
            options.EnableSensitiveDataLogging();
            options.EnableDetailedErrors();
#endif
        });

        services.AddPurchasingCore();

        services.AddSingleton<IModule, PurchasingModule>();
        services.AddHostedService<PurchasingDatabaseInitializer>();
        return services;
    }

    /// <summary>The module's services without the DbContext/hosting plumbing (also used by tests with an in-memory database).</summary>
    public static IServiceCollection AddPurchasingCore(this IServiceCollection services)
    {
        services.AddScoped<Purchasing.Application.Abstractions.IPurchasingUnitOfWork, Purchasing.Infrastructure.Persistence.PurchasingUnitOfWork>();
        services.AddScoped<Purchasing.Application.Repositories.IPurchaseOrderRepository, Purchasing.Infrastructure.Repositories.EfPurchaseOrderRepository>();
        services.AddScoped<Purchasing.Application.Repositories.ISupplierReturnRepository, Purchasing.Infrastructure.Repositories.EfSupplierReturnRepository>();
        services.AddScoped<Purchasing.Contracts.Interfaces.IPurchaseOrderReader, Purchasing.Infrastructure.Services.PurchaseOrderReader>();
        services.AddSingleton<Platform.Application.Abstractions.Authorization.ICapabilityProvider, Purchasing.Application.Security.PurchasingCapabilityProvider>();
        services.AddTransient<Purchasing.Application.Commands.CreatePurchaseOrderCommandHandler>();
        services.AddTransient<Purchasing.Application.Commands.AddPurchaseOrderLineCommandHandler>();
        services.AddTransient<Purchasing.Application.Commands.RemovePurchaseOrderLineCommandHandler>();
        services.AddTransient<Purchasing.Application.Commands.ChangePurchaseOrderLineQuantityCommandHandler>();
        services.AddTransient<Purchasing.Application.Commands.SubmitPurchaseOrderCommandHandler>();
        services.AddTransient<Purchasing.Application.Commands.CancelPurchaseOrderCommandHandler>();
        services.AddTransient<Purchasing.Application.Commands.ReceivePurchaseOrderCommandHandler>();
        services.AddTransient<Purchasing.Application.Commands.ClosePurchaseOrderShortCommandHandler>();
        services.AddTransient<Purchasing.Application.Commands.ReturnToSupplierCommandHandler>();
        services.AddTransient<Purchasing.Application.Queries.ListSupplierReturnsQueryHandler>();
        services.AddTransient<Purchasing.Application.Queries.GetPurchaseOrderQueryHandler>();
        services.AddTransient<Purchasing.Application.Queries.ListPurchaseOrdersQueryHandler>();
        services.AddTransient<Purchasing.Application.Queries.FindOrderSuppliersQueryHandler>();
        services.AddTransient<Purchasing.Application.Queries.ListReceivingWarehousesQueryHandler>();
        return services;
    }
}
