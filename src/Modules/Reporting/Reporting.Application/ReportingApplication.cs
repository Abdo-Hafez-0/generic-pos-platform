using Customers.Contracts.Interfaces;
using Inventory.Contracts.Interfaces;
using Platform.Core.Results;
using Purchasing.Contracts.Interfaces;
using Reporting.Domain.Calculations;
using Reporting.Domain.ValueObjects;
using Sales.Contracts.Interfaces;
using Sales.Contracts.Models;
using Suppliers.Contracts.Interfaces;

namespace Reporting.Application.DTOs
{
    public sealed record DailySalesDto(DateOnly Date, int SaleCount, decimal Total);

    public sealed record SalesReportDto(
        DateTime From, DateTime To, int SaleCount, decimal GrandTotal, decimal AverageSale, IReadOnlyList<DailySalesDto> Days, bool IsTruncated);

    public sealed record InventorySnapshotDto(int StockItemCount, int ProductCount, decimal TotalOnHand, int OutOfStockCount);

    public sealed record PurchasingOverviewDto(
        int TotalOrders, int Draft, int Submitted, int Received, int Cancelled, decimal ReceivedValue, decimal OpenValue);

    public sealed record PartySummaryDto(int Total, int Active, int Inactive);

    /// <summary>One section of the overview: available with data, or unavailable/failed with the reason.</summary>
    public sealed record OverviewSection<T>(bool IsAvailable, T? Data, string? ErrorCode, string? Message)
    {
        public static OverviewSection<T> From(Result<T> result)
            => result.IsSuccess
                ? new(true, result.Value, null, null)
                : new(false, default, result.Error.Code, result.Error.Description);
    }

    public sealed record BusinessOverviewDto(
        DateTime From,
        DateTime To,
        OverviewSection<SalesReportDto> Sales,
        OverviewSection<InventorySnapshotDto> Inventory,
        OverviewSection<PurchasingOverviewDto> Purchasing,
        OverviewSection<PartySummaryDto> Customers,
        OverviewSection<PartySummaryDto> Suppliers);
}

namespace Reporting.Application
{
    /// <summary>Shared error for a report whose source module is not installed (an optional dependency, not a fault).</summary>
    public static class ReportingErrors
    {
        public const string SourceUnavailable = "Reporting.Source.Unavailable";

        public static Error Unavailable(string module)
            => Error.Conflict(SourceUnavailable, $"The {module} module is not installed, so this report is unavailable.");
    }
}

namespace Reporting.Application.Queries
{
    using Reporting.Application.DTOs;

    /// <summary>
    /// Completed sales in a range, from Sales.Contracts. Sales exposes only a "recent sales" list, so at most <see cref="MaxSalesScanned"/>
    /// sales are scanned; when that window may not reach back to the start of the range the report says it is truncated.
    /// </summary>
    public sealed record GetSalesReportQuery(DateTime From, DateTime To);

    public sealed class GetSalesReportQueryHandler(ISalesReader? salesReader = null)
    {
        public const int MaxSalesScanned = 2000;

        public async Task<Result<SalesReportDto>> HandleAsync(GetSalesReportQuery query, CancellationToken cancellationToken = default)
        {
            var range = DateRange.Create(query.From, query.To);
            if (range.IsFailure) return Result.Failure<SalesReportDto>(range.Error);
            if (salesReader is null) return Result.Failure<SalesReportDto>(ReportingErrors.Unavailable("Sales"));

            var recent = await salesReader.GetRecentAsync(MaxSalesScanned, cancellationToken);

            var facts = recent
                .Where(s => s.Status == SaleStatusContract.Completed && s.CompletedAt is not null)
                .Select(s => new SaleFact(s.CompletedAt!.Value, s.GrandTotal));
            var figures = SalesCalculator.Calculate(facts, range.Value);

            // The list is ordered by creation time, newest first. If it is full and its oldest sale is newer than the range start,
            // sales created before that point (which may have completed inside the range) were not scanned.
            var truncated = recent.Count >= MaxSalesScanned && recent.Min(s => s.CreatedAt) > range.Value.From;

            return Result.Success(new SalesReportDto(
                range.Value.From, range.Value.To, figures.SaleCount, figures.GrandTotal, figures.AverageSale,
                figures.Days.Select(d => new DailySalesDto(d.Date, d.SaleCount, d.Total)).ToList(), truncated));
        }
    }

    public sealed record GetInventorySnapshotQuery;

    public sealed class GetInventorySnapshotQueryHandler(IInventoryReader? inventoryReader = null)
    {
        public async Task<Result<InventorySnapshotDto>> HandleAsync(GetInventorySnapshotQuery query, CancellationToken cancellationToken = default)
        {
            if (inventoryReader is null) return Result.Failure<InventorySnapshotDto>(ReportingErrors.Unavailable("Inventory"));

            var levels = await inventoryReader.GetAllStockLevelsAsync(cancellationToken);
            var figures = StockCalculator.Calculate(levels.Select(l => new StockFact(l.CatalogProductId, l.OnHand)));
            return Result.Success(new InventorySnapshotDto(figures.StockItemCount, figures.ProductCount, figures.TotalOnHand, figures.OutOfStockCount));
        }
    }

    public sealed record GetPurchasingOverviewQuery;

    public sealed class GetPurchasingOverviewQueryHandler(IPurchaseOrderReader? purchaseOrderReader = null)
    {
        public async Task<Result<PurchasingOverviewDto>> HandleAsync(GetPurchasingOverviewQuery query, CancellationToken cancellationToken = default)
        {
            if (purchaseOrderReader is null) return Result.Failure<PurchasingOverviewDto>(ReportingErrors.Unavailable("Purchasing"));

            var s = await purchaseOrderReader.GetSummaryAsync(cancellationToken);
            return Result.Success(new PurchasingOverviewDto(s.TotalOrders, s.Draft, s.Submitted, s.Received, s.Cancelled, s.ReceivedValue, s.OpenValue));
        }
    }

    public sealed record GetCustomerSummaryQuery;

    public sealed class GetCustomerSummaryQueryHandler(ICustomerReader? customerReader = null)
    {
        public async Task<Result<PartySummaryDto>> HandleAsync(GetCustomerSummaryQuery query, CancellationToken cancellationToken = default)
        {
            if (customerReader is null) return Result.Failure<PartySummaryDto>(ReportingErrors.Unavailable("Customers"));

            var s = await customerReader.GetSummaryAsync(cancellationToken);
            return Result.Success(new PartySummaryDto(s.Total, s.Active, s.Inactive));
        }
    }

    public sealed record GetSupplierSummaryQuery;

    public sealed class GetSupplierSummaryQueryHandler(ISupplierReader? supplierReader = null)
    {
        public async Task<Result<PartySummaryDto>> HandleAsync(GetSupplierSummaryQuery query, CancellationToken cancellationToken = default)
        {
            if (supplierReader is null) return Result.Failure<PartySummaryDto>(ReportingErrors.Unavailable("Suppliers"));

            var s = await supplierReader.GetSummaryAsync(cancellationToken);
            return Result.Success(new PartySummaryDto(s.Total, s.Active, s.Inactive));
        }
    }

    /// <summary>All reports at once. Each section succeeds or fails on its own; only an invalid range fails the whole overview.</summary>
    public sealed record GetBusinessOverviewQuery(DateTime From, DateTime To);

    public sealed class GetBusinessOverviewQueryHandler(
        GetSalesReportQueryHandler sales,
        GetInventorySnapshotQueryHandler inventory,
        GetPurchasingOverviewQueryHandler purchasing,
        GetCustomerSummaryQueryHandler customers,
        GetSupplierSummaryQueryHandler suppliers)
    {
        public async Task<Result<BusinessOverviewDto>> HandleAsync(GetBusinessOverviewQuery query, CancellationToken cancellationToken = default)
        {
            var range = DateRange.Create(query.From, query.To);
            if (range.IsFailure) return Result.Failure<BusinessOverviewDto>(range.Error);

            return Result.Success(new BusinessOverviewDto(
                range.Value.From,
                range.Value.To,
                OverviewSection<SalesReportDto>.From(await sales.HandleAsync(new GetSalesReportQuery(query.From, query.To), cancellationToken)),
                OverviewSection<InventorySnapshotDto>.From(await inventory.HandleAsync(new GetInventorySnapshotQuery(), cancellationToken)),
                OverviewSection<PurchasingOverviewDto>.From(await purchasing.HandleAsync(new GetPurchasingOverviewQuery(), cancellationToken)),
                OverviewSection<PartySummaryDto>.From(await customers.HandleAsync(new GetCustomerSummaryQuery(), cancellationToken)),
                OverviewSection<PartySummaryDto>.From(await suppliers.HandleAsync(new GetSupplierSummaryQuery(), cancellationToken))));
        }
    }
}
