using Audit.Contracts.Interfaces;
using CashManagement.Contracts.Interfaces;
using Customers.Contracts.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Payments.Contracts.Interfaces;
using POS.Contracts.Interfaces;
using Pricing.Contracts.Interfaces;
using Purchasing.Contracts.Interfaces;
using Reporting.Contracts.Interfaces;
using Suppliers.Contracts.Interfaces;
using Users.Contracts.Interfaces;

namespace Integration.Tests;

/// <summary>
/// Every Stage 8 module is optional: the real host starts and works with any of them absent, and a module that is present
/// brings only its own tables and contracts. Nothing here is stubbed.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class OptionalModuleIsolationTests
{
    private static readonly Dictionary<string, string> Prefixes = new()
    {
        ["Customers"] = "cus_", ["Suppliers"] = "sup_", ["Purchasing"] = "pur_", ["Pricing"] = "pri_", ["Payments"] = "pay_",
        ["Users"] = "usr_", ["Audit"] = "aud_", ["CashManagement"] = "cash_", ["Reporting"] = ""
    };

    /// <summary>The contract each Stage 8 module publishes (resolved from the real container).</summary>
    private static readonly Dictionary<string, Type> Contracts = new()
    {
        ["Customers"] = typeof(ICustomerLookup),
        ["Suppliers"] = typeof(ISupplierLookup),
        ["Purchasing"] = typeof(IPurchaseOrderReader),
        ["Pricing"] = typeof(IPriceResolver),
        ["Payments"] = typeof(IPaymentService),
        ["Users"] = typeof(IUserLookup),
        ["Audit"] = typeof(IAuditRecorder),
        ["CashManagement"] = typeof(ICashMovementRecorder),
        ["Reporting"] = typeof(IReportProvider)
    };

    public static IEnumerable<object[]> Stage8() => IntegrationHost.Stage8Modules.Select(m => new object[] { m });

    [Fact]
    public async Task TheCoreModulesAloneStart_WithNoStage8TablesOrContracts()
    {
        await using var host = await IntegrationHost.StartAsync(IntegrationHost.CoreModules);

        var tables = await host.GetTablesAsync();

        Assert.Contains(tables, t => t.StartsWith("cat_", StringComparison.Ordinal));
        Assert.Contains(tables, t => t.StartsWith("pos_", StringComparison.Ordinal));
        foreach (var prefix in Prefixes.Values.Where(p => p.Length > 0))
            Assert.DoesNotContain(tables, t => t.StartsWith(prefix, StringComparison.Ordinal));
        foreach (var contract in Contracts.Values)
            Assert.Null(host.Services.GetService(contract));
        Assert.NotNull(host.Services.CreateScope().ServiceProvider.GetService<IPOSService>());
    }

    [Theory]
    [MemberData(nameof(Stage8))]
    public async Task ASingleStage8ModuleCanBeAddedToTheCore_AndBringsOnlyItsOwnTablesAndContract(string module)
    {
        string[] modules = [.. IntegrationHost.CoreModules, .. IntegrationHost.Stage8DependenciesOf(module), module];
        await using var host = await IntegrationHost.StartAsync(modules);

        var tables = await host.GetTablesAsync();

        using var scope = host.Services.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetService(Contracts[module]));
        var ownPrefix = Prefixes[module];
        if (ownPrefix.Length > 0) Assert.Contains(tables, t => t.StartsWith(ownPrefix, StringComparison.Ordinal));
        var foreign = Prefixes.Where(p => p.Value.Length > 0 && p.Key != module && !IntegrationHost.Stage8DependenciesOf(module).Contains(p.Key));
        foreach (var (otherModule, prefix) in foreign)
            Assert.DoesNotContain(tables, t => t.StartsWith(prefix, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Stage8))]
    public async Task TheFullSetStartsWithAnyOneStage8ModuleRemoved(string removed)
    {
        // a module that declares the removed one as a dependency (Purchasing needs Suppliers) leaves with it
        var gone = IntegrationHost.Stage8Modules.Where(m => m == removed || IntegrationHost.Stage8DependenciesOf(m).Contains(removed)).ToHashSet();
        string[] modules = [.. IntegrationHost.CoreModules, .. IntegrationHost.Stage8Modules.Where(m => !gone.Contains(m))];

        await using var host = await IntegrationHost.StartAsync(modules);

        using var scope = host.Services.CreateScope();
        foreach (var (module, contract) in Contracts)
            Assert.Equal(!gone.Contains(module), scope.ServiceProvider.GetService(contract) is not null);
        Assert.NotNull(scope.ServiceProvider.GetService<IPOSService>());
        if (Prefixes[removed].Length > 0)
            Assert.DoesNotContain(await host.GetTablesAsync(), t => t.StartsWith(Prefixes[removed], StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReportingAloneStartsAndReportsEverySectionAsUnavailable_NotAsAnError()
    {
        await using var host = await IntegrationHost.StartAsync(["Reporting"]);

        using var scope = host.Services.CreateScope();
        var overview = await scope.ServiceProvider.GetRequiredService<IReportProvider>()
            .GetBusinessOverviewAsync(DateTime.UtcNow.AddDays(-7), DateTime.UtcNow);

        Assert.True(overview.Sales.IsUnavailable);
        Assert.True(overview.Inventory.IsUnavailable);
        Assert.True(overview.Purchasing.IsUnavailable);
        Assert.True(overview.Customers.IsUnavailable);
        Assert.True(overview.Suppliers.IsUnavailable);
        Assert.Empty(await host.GetTablesAsync());
    }

    [Fact]
    public async Task ReportingReadsWhicheverModulesArePresent()
    {
        await using var host = await IntegrationHost.StartAsync([.. IntegrationHost.CoreModules, "Customers", "Reporting"]);

        using var scope = host.Services.CreateScope();
        var overview = await scope.ServiceProvider.GetRequiredService<IReportProvider>()
            .GetBusinessOverviewAsync(DateTime.UtcNow.AddDays(-7), DateTime.UtcNow);

        Assert.True(overview.Sales.IsSuccess);
        Assert.Equal(0, overview.Sales.Data!.SaleCount);
        Assert.True(overview.Inventory.IsSuccess);
        Assert.True(overview.Customers.IsSuccess);
        Assert.Equal(0, overview.Customers.Data!.Total);
        Assert.True(overview.Purchasing.IsUnavailable);
        Assert.True(overview.Suppliers.IsUnavailable);
    }
}
