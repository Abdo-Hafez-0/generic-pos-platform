using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using POS.Application.Devices;
using Platform.Core.Modules;
using Platform.Infrastructure.Persistence;
using POS.Application.Abstractions;
using POS.Application.Commands;
using POS.Application.Queries;
using POS.Application.Repositories;
using POS.Contracts.Interfaces;
using POS.Infrastructure.Module;
using POS.Infrastructure.Persistence;
using POS.Infrastructure.Repositories;
using POS.Infrastructure.Services;

namespace POS.Infrastructure.DependencyInjection;

/// <summary>
/// DI registration extension for POS module services.
///
/// Called by POSHostingModule.RegisterServices(). All POS DI registration stays inside POS.Infrastructure.
///
/// POS consumes Catalog (IProductLookup, IProductBarcodeResolver), Inventory (IStockAvailabilityChecker,
/// IStockIssueService) and Sales (ISalesService) ONLY through their Contracts. Those implementations are
/// registered by the respective modules' own hosting modules, which must be registered before POS.
/// </summary>
public static class POSServicesExtensions
{
    public static IServiceCollection AddPOSModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var dbOptions = new DatabaseOptions();
        configuration.GetSection(DatabaseOptions.SectionName).Bind(dbOptions);
        var connectionString = dbOptions.BuildConnectionString();

        services.AddAtomicOperations().AddDbContext<POSDbContext>((sp, options) =>
        {
            options.UseSharedSqlite(sp, connectionString, sqliteOptions =>
            {
                sqliteOptions.MigrationsAssembly(typeof(POSDbContext).Assembly.FullName);
            });

#if DEBUG
            options.EnableSensitiveDataLogging();
            options.EnableDetailedErrors();
#endif
        });

        services.AddScoped<IPosUnitOfWork, PosUnitOfWork>();
        services.AddScoped<IPosSessionRepository, EfPosSessionRepository>();
        services.AddScoped<IPosCartRepository, EfPosCartRepository>();

        services.AddScoped<IPOSService, POSService>();
        services.AddScoped<IPOSReader, POSReader>();

        // Optional peripherals: the hardware abstractions (IReceiptPrinter, ICashDrawer, ILabelPrinter, IScale, IBarcodeScanner) are
        // resolved only if a hardware module registered them; without them every device operation reports "not configured".
        var receiptOptions = new PosReceiptOptions();
        configuration.GetSection(PosReceiptOptions.SectionName).Bind(receiptOptions);
        services.AddSingleton(receiptOptions);
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IPOSDevices, POSDevices>();
        services.AddSingleton<IPOSBarcodeInput, POSBarcodeInput>();

        services.AddSingleton<IModule, POSModule>();
        services.AddHostedService<POSDatabaseInitializer>();

        services.AddSingleton<Platform.Application.Abstractions.Authorization.ICapabilityProvider, POS.Application.Security.POSCapabilityProvider>();
        services.AddTransient<OpenPosSessionCommandHandler>();
        services.AddTransient<ClosePosSessionCommandHandler>();
        services.AddTransient<StartCartCommandHandler>();
        services.AddTransient<AddProductToCartCommandHandler>();
        services.AddTransient<RemoveProductFromCartCommandHandler>();
        services.AddTransient<ChangeCartQuantityCommandHandler>();
        services.AddTransient<ClearCartCommandHandler>();
        services.AddTransient<CheckoutCartCommandHandler>();

        services.AddTransient<GetPosSessionQueryHandler>();
        services.AddTransient<GetCartQueryHandler>();
        services.AddTransient<GetCurrentCartQueryHandler>();
        services.AddTransient<GetOpenSessionForCashierQueryHandler>();
        services.AddTransient<GetSaleWarehousesQueryHandler>();

        services.AddTransient<PrintReceiptCommandHandler>();
        services.AddTransient<OpenCashDrawerCommandHandler>();
        services.AddTransient<PrintProductLabelCommandHandler>();
        services.AddTransient<ReadWeightQueryHandler>();
        services.AddTransient<GetDeviceStatusQueryHandler>();

        return services;
    }
}
