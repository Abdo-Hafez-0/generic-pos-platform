using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Core.Modules;
using Reporting.Infrastructure.Module;

namespace Reporting.Infrastructure.DependencyInjection;

/// <summary>DI registration for the Reporting module (called by ReportingHostingModule). Reporting owns no tables and has no DbContext.</summary>
public static class ReportingServicesExtensions
{
    public static IServiceCollection AddReportingModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddReportingCore();
        services.AddSingleton<IModule, ReportingModule>();
        return services;
    }

    public static IServiceCollection AddReportingCore(this IServiceCollection services)
    {
        services.AddScoped<Reporting.Contracts.Interfaces.IReportProvider, Reporting.Infrastructure.Services.ReportProvider>();
        services.AddTransient<Reporting.Application.Queries.GetSalesReportQueryHandler>();
        services.AddTransient<Reporting.Application.Queries.GetInventorySnapshotQueryHandler>();
        services.AddTransient<Reporting.Application.Queries.GetPurchasingOverviewQueryHandler>();
        services.AddTransient<Reporting.Application.Queries.GetCustomerSummaryQueryHandler>();
        services.AddTransient<Reporting.Application.Queries.GetSupplierSummaryQueryHandler>();
        services.AddTransient<Reporting.Application.Queries.GetBusinessOverviewQueryHandler>();
        return services;
    }
}
