using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Core.Modules;
using Platform.Infrastructure.Persistence;
using Payments.Infrastructure.Module;
using Payments.Infrastructure.Persistence;

namespace Payments.Infrastructure.DependencyInjection;

/// <summary>DI registration for the Payments module (called by PaymentsHostingModule).</summary>
public static class PaymentsServicesExtensions
{
    public static IServiceCollection AddPaymentsModule(this IServiceCollection services, IConfiguration configuration)
    {
        // The same physical SQLite file as every other module DbContext.
        var dbOptions = new DatabaseOptions();
        configuration.GetSection(DatabaseOptions.SectionName).Bind(dbOptions);
        var connectionString = dbOptions.BuildConnectionString();

        services.AddAtomicOperations().AddDbContext<PaymentsDbContext>((sp, options) =>
        {
            options.UseSharedSqlite(sp, connectionString, sqliteOptions =>
            {
                sqliteOptions.MigrationsAssembly(typeof(PaymentsDbContext).Assembly.FullName);
            });

#if DEBUG
            options.EnableSensitiveDataLogging();
            options.EnableDetailedErrors();
#endif
        });

        services.AddPaymentsCore();

        services.AddSingleton<IModule, PaymentsModule>();
        services.AddHostedService<PaymentsDatabaseInitializer>();
        return services;
    }

    /// <summary>The module's services without the DbContext/hosting plumbing (also used by tests with an in-memory database).</summary>
    public static IServiceCollection AddPaymentsCore(this IServiceCollection services)
    {
        services.AddScoped<Payments.Application.Abstractions.IPaymentsUnitOfWork, Payments.Infrastructure.Persistence.PaymentsUnitOfWork>();
        services.AddScoped<Payments.Application.Repositories.IPaymentRepository, Payments.Infrastructure.Repositories.EfPaymentRepository>();
        services.AddScoped<Payments.Contracts.Interfaces.IPaymentService, Payments.Infrastructure.Services.PaymentService>();
        services.AddScoped<Payments.Contracts.Interfaces.IPaymentReader, Payments.Infrastructure.Services.PaymentReader>();
        services.AddSingleton<Platform.Application.Abstractions.Authorization.ICapabilityProvider, Payments.Application.Security.PaymentsCapabilityProvider>();
        services.AddTransient<Payments.Application.Commands.RecordPaymentCommandHandler>();
        services.AddTransient<Payments.Application.Commands.VoidPaymentCommandHandler>();
        services.AddTransient<Payments.Application.Queries.GetPaymentQueryHandler>();
        services.AddTransient<Payments.Application.Queries.GetPaymentsForReferenceQueryHandler>();
        return services;
    }
}
