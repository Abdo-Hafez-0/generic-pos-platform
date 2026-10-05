using Microsoft.Extensions.DependencyInjection;
using Platform.Application.Abstractions.Authorization;
using Reporting.Application.Queries;
using Reporting.Application.Security;
using Reporting.Contracts.Interfaces;
using Reporting.Infrastructure.DependencyInjection;
using Tests.Common.Security;

namespace Reporting.Tests.Security;

public sealed class ReportingAuthorizationTests
{
    private static readonly DateTime From = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc);

    private static ServiceProvider Build(IAuthorizationService authorization)
    {
        var services = new ServiceCollection();
        services.AddReportingCore();
        services.AddSingleton(authorization);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Every_report_is_refused_without_reporting_view()
    {
        var auth = new ScriptedAuthorizationService();
        await using var sp = Build(auth);

        var results = new Platform.Core.Results.Result[]
        {
            await sp.GetRequiredService<GetSalesReportQueryHandler>().HandleAsync(new GetSalesReportQuery(From, To)),
            await sp.GetRequiredService<GetInventorySnapshotQueryHandler>().HandleAsync(new GetInventorySnapshotQuery()),
            await sp.GetRequiredService<GetPurchasingOverviewQueryHandler>().HandleAsync(new GetPurchasingOverviewQuery()),
            await sp.GetRequiredService<GetCustomerSummaryQueryHandler>().HandleAsync(new GetCustomerSummaryQuery()),
            await sp.GetRequiredService<GetSupplierSummaryQueryHandler>().HandleAsync(new GetSupplierSummaryQuery()),
            await sp.GetRequiredService<GetBusinessOverviewQueryHandler>().HandleAsync(new GetBusinessOverviewQuery(From, To))
        };

        Assert.All(results, r => Assert.Equal(SecurityErrors.ForbiddenCode, r.Error.Code));
        Assert.All(auth.Asked, c => Assert.Equal(ReportingCapabilities.ViewReports, c));
    }

    [Fact]
    public async Task The_report_contract_is_refused_too_so_it_is_not_a_way_around_the_handlers()
    {
        await using var sp = Build(new ScriptedAuthorizationService());

        var overview = await sp.GetRequiredService<IReportProvider>().GetBusinessOverviewAsync(From, To);

        Assert.False(overview.Sales.IsSuccess);
        Assert.Equal(SecurityErrors.ForbiddenCode, overview.Sales.ErrorCode);
    }

    [Fact]
    public async Task With_reporting_view_a_report_runs_and_reports_missing_modules_as_unavailable()
    {
        await using var sp = Build(new ScriptedAuthorizationService(ReportingCapabilities.ViewReports));

        var result = await sp.GetRequiredService<GetSalesReportQueryHandler>().HandleAsync(new GetSalesReportQuery(From, To));

        Assert.Equal(Reporting.Application.ReportingErrors.SourceUnavailable, result.Error.Code); // not a security refusal
    }

    [Fact]
    public void Viewing_reports_stays_available_in_every_license_state_because_the_data_belongs_to_the_customer()
    {
        var catalog = new CapabilityCatalog([new ReportingCapabilityProvider()]);

        Assert.Equal(LicenseRequirement.None, catalog.Find(ReportingCapabilities.ViewReports)!.License);
        Assert.Equal("reporting", catalog.Find(ReportingCapabilities.ViewReports)!.Module);
    }
}
