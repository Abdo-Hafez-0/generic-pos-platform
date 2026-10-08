using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Core.Modules;
using Platform.Infrastructure.Persistence;
using Pricing.Infrastructure.Module;
using Pricing.Infrastructure.Persistence;

namespace Pricing.Infrastructure.DependencyInjection;

/// <summary>DI registration for the Pricing module (called by PricingHostingModule).</summary>
public static class PricingServicesExtensions
{
    public static IServiceCollection AddPricingModule(this IServiceCollection services, IConfiguration configuration)
    {
        // The same physical SQLite file as every other module DbContext.
        var dbOptions = new DatabaseOptions();
        configuration.GetSection(DatabaseOptions.SectionName).Bind(dbOptions);
        var connectionString = dbOptions.BuildConnectionString();

        services.AddAtomicOperations().AddDbContext<PricingDbContext>((sp, options) =>
        {
            options.UseSharedSqlite(sp, connectionString, sqliteOptions =>
            {
                sqliteOptions.MigrationsAssembly(typeof(PricingDbContext).Assembly.FullName);
            });

#if DEBUG
            options.EnableSensitiveDataLogging();
            options.EnableDetailedErrors();
#endif
        });

        services.AddPricingCore();

        services.AddSingleton<IModule, PricingModule>();
        services.AddHostedService<PricingDatabaseInitializer>();
        return services;
    }

    /// <summary>The module's services without the DbContext/hosting plumbing (also used by tests with an in-memory database).</summary>
    public static IServiceCollection AddPricingCore(this IServiceCollection services)
    {
        services.AddScoped<Pricing.Application.Abstractions.IPricingUnitOfWork, Pricing.Infrastructure.Persistence.PricingUnitOfWork>();
        services.AddScoped<Pricing.Application.Repositories.IPriceListRepository, Pricing.Infrastructure.Repositories.EfPriceListRepository>();
        services.AddScoped<Pricing.Application.Repositories.IPriceRepository, Pricing.Infrastructure.Repositories.EfPriceRepository>();
        services.AddScoped<Pricing.Contracts.Interfaces.IPriceResolver, Pricing.Infrastructure.Services.PriceResolver>();
        // FIX-08a: tax rates
        services.AddScoped<Pricing.Application.Repositories.ITaxRateRepository, Pricing.Infrastructure.Repositories.EfTaxRateRepository>();
        services.AddScoped<Pricing.Application.Repositories.IProductTaxRateRepository, Pricing.Infrastructure.Repositories.EfProductTaxRateRepository>();
        services.AddScoped<Pricing.Contracts.Interfaces.ITaxRateResolver, Pricing.Infrastructure.Services.TaxRateResolver>();
        services.AddTransient<Pricing.Application.Commands.CreateTaxRateCommandHandler>();
        services.AddTransient<Pricing.Application.Commands.UpdateTaxRateCommandHandler>();
        services.AddTransient<Pricing.Application.Commands.SetDefaultTaxRateCommandHandler>();
        services.AddTransient<Pricing.Application.Commands.DeactivateTaxRateCommandHandler>();
        services.AddTransient<Pricing.Application.Commands.SetProductTaxRateCommandHandler>();
        services.AddTransient<Pricing.Application.Queries.ListTaxRatesQueryHandler>();
        services.AddTransient<Pricing.Application.Queries.GetProductTaxQueryHandler>();
        services.AddSingleton<Platform.Application.Abstractions.Authorization.ICapabilityProvider, Pricing.Application.Security.PricingCapabilityProvider>();
        services.AddTransient<Pricing.Application.Commands.CreatePriceListCommandHandler>();
        services.AddTransient<Pricing.Application.Commands.SetDefaultPriceListCommandHandler>();
        services.AddTransient<Pricing.Application.Commands.DeactivatePriceListCommandHandler>();
        services.AddTransient<Pricing.Application.Commands.CreatePriceCommandHandler>();
        services.AddTransient<Pricing.Application.Commands.UpdatePriceCommandHandler>();
        services.AddTransient<Pricing.Application.Commands.DeactivatePriceCommandHandler>();
        services.AddTransient<Pricing.Application.Queries.GetPriceQueryHandler>();
        services.AddTransient<Pricing.Application.Queries.ListPricesForProductQueryHandler>();
        services.AddTransient<Pricing.Application.Queries.ListPriceListsQueryHandler>();
        services.AddTransient<Pricing.Application.Queries.GetCurrentPriceQueryHandler>();
        services.AddTransient<Pricing.Application.Queries.FindPricingProductQueryHandler>();
        return services;
    }
}
