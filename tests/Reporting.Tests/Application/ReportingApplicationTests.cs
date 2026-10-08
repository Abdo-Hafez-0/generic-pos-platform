using Customers.Contracts.Interfaces;
using Customers.Contracts.Models;
using Inventory.Contracts.Interfaces;
using Inventory.Contracts.Models;
using Microsoft.Extensions.DependencyInjection;
using Purchasing.Contracts.Interfaces;
using Purchasing.Contracts.Models;
using Reporting.Application.Queries;
using Reporting.Contracts.Interfaces;
using Reporting.Infrastructure.DependencyInjection;
using Sales.Contracts.Interfaces;
using Sales.Contracts.Models;
using Suppliers.Contracts.Interfaces;
using Suppliers.Contracts.Models;

namespace Reporting.Tests.Application;

public sealed class ReportingApplicationTests
{
    private static readonly DateTime Jan1 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Jan31 = new(2026, 1, 31, 23, 59, 59, DateTimeKind.Utc);

    // ------------------------------------------------------------------ stubs of the other modules' read contracts

    private sealed class StubSales(IReadOnlyList<SaleSummaryResult> sales) : ISalesReader
    {
        public int LastLimit { get; private set; }

        public Task<SaleSummaryResult?> FindByIdAsync(Guid saleId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<SaleSummaryResult>> GetRecentAsync(int limit = 50, CancellationToken cancellationToken = default)
        {
            LastLimit = limit;
            return Task.FromResult<IReadOnlyList<SaleSummaryResult>>(sales.Take(limit).ToList());
        }
    }

    private sealed class StubInventory(IReadOnlyList<StockLevelDto> levels) : IInventoryReader
    {
        public Task<StockLevelDto?> GetStockLevelAsync(Guid stockItemId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<StockLevelDto?> GetStockLevelByProductAsync(Guid catalogProductId, Guid warehouseId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<StockLevelDto>> GetAllStockLevelsAsync(CancellationToken cancellationToken = default) => Task.FromResult(levels);

        public Task<IReadOnlyList<WarehouseDto>> GetAllWarehousesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubPurchasing(PurchaseSummaryResult summary) : IPurchaseOrderReader
    {
        public Task<PurchaseOrderResult?> GetAsync(Guid orderId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<PurchaseOrderResult>> ListRecentAsync(int limit = 50, PurchaseOrderStatusContract? status = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PurchaseSummaryResult> GetSummaryAsync(CancellationToken cancellationToken = default) => Task.FromResult(summary);
    }

    private sealed class StubCustomers(CustomerSummaryResult summary) : ICustomerReader
    {
        public Task<IReadOnlyList<CustomerLookupResult>> SearchAsync(string text, int limit = 25, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CustomerSummaryResult> GetSummaryAsync(CancellationToken cancellationToken = default) => Task.FromResult(summary);
    }

    private sealed class StubSuppliers(SupplierSummaryResult summary) : ISupplierReader
    {
        public Task<IReadOnlyList<SupplierLookupResult>> SearchAsync(string text, int limit = 25, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SupplierSummaryResult> GetSummaryAsync(CancellationToken cancellationToken = default) => Task.FromResult(summary);
    }

    private static SaleSummaryResult Sale(SaleStatusContract status, decimal total, DateTime? completedAt, DateTime? createdAt = null)
        => new(Guid.NewGuid(), status, null, total, 1, createdAt ?? (completedAt ?? Jan1).AddMinutes(-5), completedAt);

    private static ServiceProvider Build(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddReportingCore();
        // These tests are about report content; the authorization boundary is covered in ReportingAuthorizationTests.
        services.AddSingleton<Platform.Application.Abstractions.Authorization.IAuthorizationService, global::Tests.Common.Security.AllowAllAuthorizationService>();
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    // ------------------------------------------------------------------ sales report

    [Fact]
    public async Task SalesReport_CountsOnlyCompletedSalesInsideTheRange()
    {
        var sales = new[]
        {
            Sale(SaleStatusContract.Completed, 10m, Jan1.AddDays(2)),
            Sale(SaleStatusContract.Completed, 30m, Jan1.AddDays(5)),
            Sale(SaleStatusContract.Cancelled, 500m, Jan1.AddDays(3)),
            Sale(SaleStatusContract.Draft, 500m, null),
            Sale(SaleStatusContract.Confirmed, 500m, null),
            Sale(SaleStatusContract.Completed, 999m, Jan1.AddDays(60))   // outside the range
        };
        await using var sp = Build(s => s.AddSingleton<ISalesReader>(new StubSales(sales)));

        var r = await sp.GetRequiredService<GetSalesReportQueryHandler>().HandleAsync(new GetSalesReportQuery(Jan1, Jan31));

        Assert.True(r.IsSuccess, r.IsFailure ? r.Error.ToString() : null);
        Assert.Equal(2, r.Value.SaleCount);
        Assert.Equal(40m, r.Value.GrandTotal);
        Assert.Equal(20m, r.Value.AverageSale);
        Assert.Equal(31, r.Value.Days.Count);
        Assert.False(r.Value.IsTruncated);
    }

    [Fact]
    public async Task SalesReport_RejectsABadRange_BeforeTouchingSales()
    {
        await using var sp = Build();
        var handler = sp.GetRequiredService<GetSalesReportQueryHandler>();

        Assert.Equal("Reporting.Range.Invalid", (await handler.HandleAsync(new GetSalesReportQuery(Jan31, Jan1))).Error.Code);
        Assert.Equal("Reporting.Range.TooLong", (await handler.HandleAsync(new GetSalesReportQuery(Jan1, Jan1.AddDays(400)))).Error.Code);
    }

    [Fact]
    public async Task SalesReport_WithoutTheSalesModule_IsUnavailable()
    {
        await using var sp = Build();

        var r = await sp.GetRequiredService<GetSalesReportQueryHandler>().HandleAsync(new GetSalesReportQuery(Jan1, Jan31));

        Assert.True(r.IsFailure);
        Assert.Equal("Reporting.Source.Unavailable", r.Error.Code);
    }

    [Fact]
    public async Task SalesReport_ScansABoundedWindow()
    {
        var stub = new StubSales([]);
        await using var sp = Build(s => s.AddSingleton<ISalesReader>(stub));

        await sp.GetRequiredService<GetSalesReportQueryHandler>().HandleAsync(new GetSalesReportQuery(Jan1, Jan31));

        Assert.Equal(GetSalesReportQueryHandler.MaxSalesScanned, stub.LastLimit);
    }

    [Fact]
    public async Task SalesReport_IsTruncated_WhenTheScanWindowIsFullAndDoesNotReachTheRangeStart()
    {
        // 2000 sales, all created after the start of the range: older sales (possibly completed in range) were not scanned
        var sales = Enumerable.Range(0, GetSalesReportQueryHandler.MaxSalesScanned)
            .Select(i => Sale(SaleStatusContract.Completed, 1m, Jan1.AddDays(10), createdAt: Jan1.AddDays(9)))
            .ToList();
        await using var sp = Build(s => s.AddSingleton<ISalesReader>(new StubSales(sales)));

        var r = await sp.GetRequiredService<GetSalesReportQueryHandler>().HandleAsync(new GetSalesReportQuery(Jan1, Jan31));

        Assert.True(r.Value.IsTruncated);
        Assert.Equal(GetSalesReportQueryHandler.MaxSalesScanned, r.Value.SaleCount);
    }

    [Fact]
    public async Task SalesReport_IsNotTruncated_WhenTheWindowIsFullButReachesBackBeforeTheRange()
    {
        var sales = Enumerable.Range(0, GetSalesReportQueryHandler.MaxSalesScanned)
            .Select(i => Sale(SaleStatusContract.Completed, 1m, Jan1.AddDays(10), createdAt: i == 0 ? Jan1.AddDays(-30) : Jan1.AddDays(9)))
            .ToList();
        await using var sp = Build(s => s.AddSingleton<ISalesReader>(new StubSales(sales)));

        var r = await sp.GetRequiredService<GetSalesReportQueryHandler>().HandleAsync(new GetSalesReportQuery(Jan1, Jan31));

        Assert.False(r.Value.IsTruncated);
    }

    // ------------------------------------------------------------------ the other reports

    [Fact]
    public async Task InventorySnapshot_SummarisesStockLevels()
    {
        var p1 = Guid.NewGuid();
        var p2 = Guid.NewGuid();
        var w = Guid.NewGuid();
        var levels = new[]
        {
            new StockLevelDto(Guid.NewGuid(), p1, w, null, 7m),
            new StockLevelDto(Guid.NewGuid(), p2, w, null, 0m)
        };
        await using var sp = Build(s => s.AddSingleton<IInventoryReader>(new StubInventory(levels)));

        var r = await sp.GetRequiredService<GetInventorySnapshotQueryHandler>().HandleAsync(new GetInventorySnapshotQuery());

        Assert.Equal(2, r.Value.StockItemCount);
        Assert.Equal(2, r.Value.ProductCount);
        Assert.Equal(7m, r.Value.TotalOnHand);
        Assert.Equal(1, r.Value.OutOfStockCount);
    }

    [Fact]
    public async Task PurchasingOverview_PassesTheSummaryThrough()
    {
        await using var sp = Build(s => s.AddSingleton<IPurchaseOrderReader>(new StubPurchasing(new PurchaseSummaryResult(10, 1, 2, 5, 2, 500m, 120m))));

        var r = await sp.GetRequiredService<GetPurchasingOverviewQueryHandler>().HandleAsync(new GetPurchasingOverviewQuery());

        Assert.Equal(10, r.Value.TotalOrders);
        Assert.Equal(5, r.Value.Received);
        Assert.Equal(500m, r.Value.ReceivedValue);
        Assert.Equal(120m, r.Value.OpenValue);
    }

    [Fact]
    public async Task PurchasingOverview_CountsPartlyReceivedAsAwaitingGoods_AndClosedShortAsDone()
    {
        // FIX-09: 10 orders = 1 draft, 2 placed + 1 partly received, 3 received + 1 closed short, 2 cancelled
        await using var sp = Build(s => s.AddSingleton<IPurchaseOrderReader>(new StubPurchasing(
            new PurchaseSummaryResult(10, 1, 2, 3, 2, 500m, 120m, PartiallyReceived: 1, Closed: 1))));

        var r = await sp.GetRequiredService<GetPurchasingOverviewQueryHandler>().HandleAsync(new GetPurchasingOverviewQuery());

        Assert.Equal((1, 3, 4, 2), (r.Value.Draft, r.Value.Submitted, r.Value.Received, r.Value.Cancelled));
        Assert.Equal(r.Value.TotalOrders, r.Value.Draft + r.Value.Submitted + r.Value.Received + r.Value.Cancelled);
    }

    [Fact]
    public async Task CustomerAndSupplierSummaries_PassTheCountsThrough()
    {
        await using var sp = Build(s =>
        {
            s.AddSingleton<ICustomerReader>(new StubCustomers(new CustomerSummaryResult(9, 7, 2)));
            s.AddSingleton<ISupplierReader>(new StubSuppliers(new SupplierSummaryResult(4, 3, 1)));
        });

        var customers = await sp.GetRequiredService<GetCustomerSummaryQueryHandler>().HandleAsync(new GetCustomerSummaryQuery());
        var suppliers = await sp.GetRequiredService<GetSupplierSummaryQueryHandler>().HandleAsync(new GetSupplierSummaryQuery());

        Assert.Equal((9, 7, 2), (customers.Value.Total, customers.Value.Active, customers.Value.Inactive));
        Assert.Equal((4, 3, 1), (suppliers.Value.Total, suppliers.Value.Active, suppliers.Value.Inactive));
    }

    [Fact]
    public async Task EachReport_IsUnavailable_WhenItsModuleIsNotInstalled()
    {
        await using var sp = Build();

        Assert.Equal("Reporting.Source.Unavailable", (await sp.GetRequiredService<GetInventorySnapshotQueryHandler>().HandleAsync(new GetInventorySnapshotQuery())).Error.Code);
        Assert.Equal("Reporting.Source.Unavailable", (await sp.GetRequiredService<GetPurchasingOverviewQueryHandler>().HandleAsync(new GetPurchasingOverviewQuery())).Error.Code);
        Assert.Equal("Reporting.Source.Unavailable", (await sp.GetRequiredService<GetCustomerSummaryQueryHandler>().HandleAsync(new GetCustomerSummaryQuery())).Error.Code);
        Assert.Equal("Reporting.Source.Unavailable", (await sp.GetRequiredService<GetSupplierSummaryQueryHandler>().HandleAsync(new GetSupplierSummaryQuery())).Error.Code);
    }

    // ------------------------------------------------------------------ overview

    [Fact]
    public async Task Overview_ReportsEverySectionIndependently_WhenSomeModulesAreMissing()
    {
        await using var sp = Build(s =>
        {
            s.AddSingleton<ICustomerReader>(new StubCustomers(new CustomerSummaryResult(2, 2, 0)));
            s.AddSingleton<ISalesReader>(new StubSales([Sale(SaleStatusContract.Completed, 12m, Jan1.AddDays(1))]));
        });

        var r = await sp.GetRequiredService<GetBusinessOverviewQueryHandler>().HandleAsync(new GetBusinessOverviewQuery(Jan1, Jan31));

        Assert.True(r.IsSuccess);
        Assert.True(r.Value.Sales.IsAvailable);
        Assert.Equal(12m, r.Value.Sales.Data!.GrandTotal);
        Assert.True(r.Value.Customers.IsAvailable);
        Assert.False(r.Value.Inventory.IsAvailable);
        Assert.False(r.Value.Purchasing.IsAvailable);
        Assert.False(r.Value.Suppliers.IsAvailable);
        Assert.Equal("Reporting.Source.Unavailable", r.Value.Inventory.ErrorCode);
        Assert.Contains("Inventory", r.Value.Inventory.Message);
    }

    [Fact]
    public async Task Overview_WithNoModulesAtAll_StillSucceeds_WithEverySectionUnavailable()
    {
        await using var sp = Build();

        var r = await sp.GetRequiredService<GetBusinessOverviewQueryHandler>().HandleAsync(new GetBusinessOverviewQuery(Jan1, Jan31));

        Assert.True(r.IsSuccess);
        Assert.All(new[] { r.Value.Sales.IsAvailable, r.Value.Inventory.IsAvailable, r.Value.Purchasing.IsAvailable, r.Value.Customers.IsAvailable, r.Value.Suppliers.IsAvailable }, Assert.False);
    }

    [Fact]
    public async Task Overview_WithABadRange_FailsAsAWhole()
    {
        await using var sp = Build();

        var r = await sp.GetRequiredService<GetBusinessOverviewQueryHandler>().HandleAsync(new GetBusinessOverviewQuery(Jan31, Jan1));

        Assert.Equal("Reporting.Range.Invalid", r.Error.Code);
    }

    // ------------------------------------------------------------------ contract

    [Fact]
    public async Task Contract_Provider_MapsOkUnavailableAndFailure()
    {
        await using var sp = Build(s => s.AddSingleton<ICustomerReader>(new StubCustomers(new CustomerSummaryResult(3, 2, 1))));
        var provider = sp.GetRequiredService<IReportProvider>();

        var ok = await provider.GetCustomerSummaryAsync();
        var unavailable = await provider.GetSupplierSummaryAsync();
        var failure = await provider.GetSalesReportAsync(Jan31, Jan1);

        Assert.True(ok.IsSuccess);
        Assert.Equal(3, ok.Data!.Total);
        Assert.False(unavailable.IsSuccess);
        Assert.True(unavailable.IsUnavailable);
        Assert.Equal("Reporting.Source.Unavailable", unavailable.ErrorCode);
        Assert.False(failure.IsSuccess);
        Assert.False(failure.IsUnavailable);
        Assert.Equal("Reporting.Range.Invalid", failure.ErrorCode);
    }

    [Fact]
    public async Task Contract_Provider_SalesReportCarriesTheDailyBreakdown()
    {
        var sales = new[] { Sale(SaleStatusContract.Completed, 25m, Jan1.AddDays(1).AddHours(4)) };
        await using var sp = Build(s => s.AddSingleton<ISalesReader>(new StubSales(sales)));

        var r = await sp.GetRequiredService<IReportProvider>().GetSalesReportAsync(Jan1, Jan1.AddDays(2));

        Assert.True(r.IsSuccess);
        Assert.Equal(3, r.Data!.Days.Count);
        Assert.Equal(25m, r.Data.Days[1].Total);
        Assert.Equal(1, r.Data.SaleCount);
    }

    [Fact]
    public async Task Contract_Provider_OverviewMapsEverySection()
    {
        await using var sp = Build(s =>
        {
            s.AddSingleton<IPurchaseOrderReader>(new StubPurchasing(new PurchaseSummaryResult(1, 1, 0, 0, 0, 0m, 9m)));
            s.AddSingleton<ISupplierReader>(new StubSuppliers(new SupplierSummaryResult(5, 4, 1)));
        });

        var o = await sp.GetRequiredService<IReportProvider>().GetBusinessOverviewAsync(Jan1, Jan31);

        Assert.Equal(9m, o.Purchasing.Data!.OpenValue);
        Assert.Equal(5, o.Suppliers.Data!.Total);
        Assert.True(o.Sales.IsUnavailable);
        Assert.True(o.Inventory.IsUnavailable);
        Assert.True(o.Customers.IsUnavailable);
    }

    [Fact]
    public async Task Contract_Provider_OverviewWithABadRange_FailsEverySection()
    {
        await using var sp = Build();

        var o = await sp.GetRequiredService<IReportProvider>().GetBusinessOverviewAsync(Jan31, Jan1);

        Assert.All(new[] { o.Sales.ErrorCode, o.Inventory.ErrorCode, o.Purchasing.ErrorCode, o.Customers.ErrorCode, o.Suppliers.ErrorCode }, c => Assert.Equal("Reporting.Range.Invalid", c));
        Assert.False(o.Sales.IsUnavailable);
    }

    [Fact]
    public async Task Contract_ExposesNoDomainTypes()
    {
        Assert.DoesNotContain(typeof(IReportProvider).Assembly.GetReferencedAssemblies(), a => a.Name!.StartsWith("Reporting.Domain", StringComparison.Ordinal));
        await Task.CompletedTask;
    }
}
