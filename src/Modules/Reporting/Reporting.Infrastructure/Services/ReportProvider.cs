using Platform.Core.Results;
using Reporting.Application;
using Reporting.Application.DTOs;
using Reporting.Application.Queries;
using Reporting.Contracts.Interfaces;
using Reporting.Contracts.Models;

namespace Reporting.Infrastructure.Services;

/// <summary>Implements IReportProvider from Reporting.Contracts by wrapping the query handlers. Nothing is stored.</summary>
internal sealed class ReportProvider(
    GetSalesReportQueryHandler sales,
    GetInventorySnapshotQueryHandler inventory,
    GetPurchasingOverviewQueryHandler purchasing,
    GetCustomerSummaryQueryHandler customers,
    GetSupplierSummaryQueryHandler suppliers,
    GetBusinessOverviewQueryHandler overview) : IReportProvider
{
    public async Task<ReportResult<SalesReportData>> GetSalesReportAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
        => Map(await sales.HandleAsync(new GetSalesReportQuery(from, to), cancellationToken), ToData);

    public async Task<ReportResult<InventorySnapshotData>> GetInventorySnapshotAsync(CancellationToken cancellationToken = default)
        => Map(await inventory.HandleAsync(new GetInventorySnapshotQuery(), cancellationToken), ToData);

    public async Task<ReportResult<PurchasingOverviewData>> GetPurchasingOverviewAsync(CancellationToken cancellationToken = default)
        => Map(await purchasing.HandleAsync(new GetPurchasingOverviewQuery(), cancellationToken), ToData);

    public async Task<ReportResult<PartySummaryData>> GetCustomerSummaryAsync(CancellationToken cancellationToken = default)
        => Map(await customers.HandleAsync(new GetCustomerSummaryQuery(), cancellationToken), ToData);

    public async Task<ReportResult<PartySummaryData>> GetSupplierSummaryAsync(CancellationToken cancellationToken = default)
        => Map(await suppliers.HandleAsync(new GetSupplierSummaryQuery(), cancellationToken), ToData);

    public async Task<BusinessOverviewResult> GetBusinessOverviewAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
    {
        var result = await overview.HandleAsync(new GetBusinessOverviewQuery(from, to), cancellationToken);
        if (result.IsFailure)
        {
            var failed = $"{result.Error.Description}";
            ReportResult<T> Fail<T>() => ReportResult<T>.Failure(result.Error.Code, failed);
            return new BusinessOverviewResult(from, to, Fail<SalesReportData>(), Fail<InventorySnapshotData>(), Fail<PurchasingOverviewData>(),
                Fail<PartySummaryData>(), Fail<PartySummaryData>());
        }

        var o = result.Value;
        return new BusinessOverviewResult(
            o.From, o.To,
            Section(o.Sales, ToData), Section(o.Inventory, ToData), Section(o.Purchasing, ToData),
            Section(o.Customers, ToData), Section(o.Suppliers, ToData));
    }

    private static ReportResult<TData> Map<TDto, TData>(Result<TDto> result, Func<TDto, TData> convert)
    {
        if (result.IsSuccess) return ReportResult<TData>.Ok(convert(result.Value));

        return result.Error.Code == ReportingErrors.SourceUnavailable
            ? ReportResult<TData>.Unavailable(result.Error.Description)
            : ReportResult<TData>.Failure(result.Error.Code, result.Error.Description);
    }

    private static ReportResult<TData> Section<TDto, TData>(OverviewSection<TDto> section, Func<TDto, TData> convert)
    {
        if (section.IsAvailable) return ReportResult<TData>.Ok(convert(section.Data!));

        return section.ErrorCode == ReportingErrors.SourceUnavailable
            ? ReportResult<TData>.Unavailable(section.Message ?? string.Empty)
            : ReportResult<TData>.Failure(section.ErrorCode ?? "Reporting.Failed", section.Message ?? string.Empty);
    }

    private static SalesReportData ToData(SalesReportDto d)
        => new(d.From, d.To, d.SaleCount, d.GrandTotal, d.AverageSale, d.Days.Select(x => new DailySalesData(x.Date, x.SaleCount, x.Total)).ToList(), d.IsTruncated);

    private static InventorySnapshotData ToData(InventorySnapshotDto d) => new(d.StockItemCount, d.ProductCount, d.TotalOnHand, d.OutOfStockCount);

    private static PurchasingOverviewData ToData(PurchasingOverviewDto d)
        => new(d.TotalOrders, d.Draft, d.Submitted, d.Received, d.Cancelled, d.ReceivedValue, d.OpenValue);

    private static PartySummaryData ToData(PartySummaryDto d) => new(d.Total, d.Active, d.Inactive);
}
