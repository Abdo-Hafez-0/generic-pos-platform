using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Core.Modules;
using Platform.Infrastructure.Persistence;
using Suppliers.Infrastructure.Module;
using Suppliers.Infrastructure.Persistence;

namespace Suppliers.Infrastructure.DependencyInjection;

/// <summary>DI registration for the Suppliers module (called by SuppliersHostingModule).</summary>
public static class SuppliersServicesExtensions
{
    public static IServiceCollection AddSuppliersModule(this IServiceCollection services, IConfiguration configuration)
    {
        // The same physical SQLite file as every other module DbContext.
        var dbOptions = new DatabaseOptions();
        configuration.GetSection(DatabaseOptions.SectionName).Bind(dbOptions);
        var connectionString = dbOptions.BuildConnectionString();

        services.AddDbContext<SuppliersDbContext>(options =>
        {
            options.UseSqlite(connectionString, sqliteOptions =>
            {
                sqliteOptions.MigrationsAssembly(typeof(SuppliersDbContext).Assembly.FullName);
            });

#if DEBUG
            options.EnableSensitiveDataLogging();
            options.EnableDetailedErrors();
#endif
        });

        services.AddSuppliersCore();

        services.AddSingleton<IModule, SuppliersModule>();
        services.AddHostedService<SuppliersDatabaseInitializer>();
        return services;
    }

    /// <summary>The module's services without the DbContext/hosting plumbing (also used by tests with an in-memory database).</summary>
    public static IServiceCollection AddSuppliersCore(this IServiceCollection services)
    {
        services.AddScoped<Suppliers.Application.Abstractions.ISuppliersUnitOfWork, Suppliers.Infrastructure.Persistence.SuppliersUnitOfWork>();
        services.AddScoped<Suppliers.Application.Repositories.ISupplierRepository, Suppliers.Infrastructure.Repositories.EfSupplierRepository>();
        services.AddScoped<Suppliers.Contracts.Interfaces.ISupplierLookup, Suppliers.Infrastructure.Services.SupplierLookup>();
        services.AddScoped<Suppliers.Contracts.Interfaces.ISupplierReader, Suppliers.Infrastructure.Services.SupplierReader>();
        services.AddTransient<Suppliers.Application.Commands.CreateSupplierCommandHandler>();
        services.AddTransient<Suppliers.Application.Commands.UpdateSupplierCommandHandler>();
        services.AddTransient<Suppliers.Application.Commands.DeactivateSupplierCommandHandler>();
        services.AddTransient<Suppliers.Application.Commands.ReactivateSupplierCommandHandler>();
        services.AddTransient<Suppliers.Application.Commands.AddSupplierAddressCommandHandler>();
        services.AddTransient<Suppliers.Application.Commands.RemoveSupplierAddressCommandHandler>();
        services.AddTransient<Suppliers.Application.Commands.AddSupplierContactCommandHandler>();
        services.AddTransient<Suppliers.Application.Commands.RemoveSupplierContactCommandHandler>();
        services.AddTransient<Suppliers.Application.Queries.GetSupplierByIdQueryHandler>();
        services.AddTransient<Suppliers.Application.Queries.ListSuppliersQueryHandler>();
        services.AddTransient<Suppliers.Application.Queries.SearchSuppliersQueryHandler>();
        return services;
    }
}
