using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Core.Modules;
using Platform.Infrastructure.Persistence;
using Customers.Infrastructure.Module;
using Customers.Infrastructure.Persistence;

namespace Customers.Infrastructure.DependencyInjection;

/// <summary>DI registration for the Customers module (called by CustomersHostingModule).</summary>
public static class CustomersServicesExtensions
{
    public static IServiceCollection AddCustomersModule(this IServiceCollection services, IConfiguration configuration)
    {
        // The same physical SQLite file as every other module DbContext.
        var dbOptions = new DatabaseOptions();
        configuration.GetSection(DatabaseOptions.SectionName).Bind(dbOptions);
        var connectionString = dbOptions.BuildConnectionString();

        services.AddDbContext<CustomersDbContext>(options =>
        {
            options.UseSqlite(connectionString, sqliteOptions =>
            {
                sqliteOptions.MigrationsAssembly(typeof(CustomersDbContext).Assembly.FullName);
            });

#if DEBUG
            options.EnableSensitiveDataLogging();
            options.EnableDetailedErrors();
#endif
        });

        services.AddCustomersCore();

        services.AddSingleton<IModule, CustomersModule>();
        services.AddHostedService<CustomersDatabaseInitializer>();
        return services;
    }

    /// <summary>The module's services without the DbContext/hosting plumbing (also used by tests with an in-memory database).</summary>
    public static IServiceCollection AddCustomersCore(this IServiceCollection services)
    {
        services.AddScoped<Customers.Application.Abstractions.ICustomersUnitOfWork, Customers.Infrastructure.Persistence.CustomersUnitOfWork>();
        services.AddScoped<Customers.Application.Repositories.ICustomerRepository, Customers.Infrastructure.Repositories.EfCustomerRepository>();
        services.AddScoped<Customers.Contracts.Interfaces.ICustomerLookup, Customers.Infrastructure.Services.CustomerLookup>();
        services.AddScoped<Customers.Contracts.Interfaces.ICustomerReader, Customers.Infrastructure.Services.CustomerReader>();
        services.AddSingleton<Platform.Application.Abstractions.Authorization.ICapabilityProvider, Customers.Application.Security.CustomersCapabilityProvider>();
        services.AddTransient<Customers.Application.Commands.CreateCustomerCommandHandler>();
        services.AddTransient<Customers.Application.Commands.UpdateCustomerCommandHandler>();
        services.AddTransient<Customers.Application.Commands.DeactivateCustomerCommandHandler>();
        services.AddTransient<Customers.Application.Commands.ReactivateCustomerCommandHandler>();
        services.AddTransient<Customers.Application.Commands.AddCustomerAddressCommandHandler>();
        services.AddTransient<Customers.Application.Commands.RemoveCustomerAddressCommandHandler>();
        services.AddTransient<Customers.Application.Commands.AddCustomerContactCommandHandler>();
        services.AddTransient<Customers.Application.Commands.RemoveCustomerContactCommandHandler>();
        services.AddTransient<Customers.Application.Queries.GetCustomerByIdQueryHandler>();
        services.AddTransient<Customers.Application.Queries.ListCustomersQueryHandler>();
        services.AddTransient<Customers.Application.Queries.SearchCustomersQueryHandler>();
        return services;
    }
}
