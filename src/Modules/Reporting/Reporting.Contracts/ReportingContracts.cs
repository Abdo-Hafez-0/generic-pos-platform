namespace Reporting.Contracts.Models
{
    /// <summary>
    /// The outcome of one report. A report is Unavailable (not a failure) when the module it reads from is not installed:
    /// Reporting works with whatever modules exist.
    /// </summary>
    public sealed record ReportResult<T>(bool IsSuccess, bool IsUnavailable, T? Data, string? ErrorCode, string? ErrorMessage)
    {
        public const string UnavailableCode = "Reporting.Source.Unavailable";

        public static ReportResult<T> Ok(T data) => new(true, false, data, null, null);

        public static ReportResult<T> Unavailable(string message) => new(false, true, default, UnavailableCode, message);

        public static ReportResult<T> Failure(string errorCode, string errorMessage) => new(false, false, default, errorCode, errorMessage);
    }

    public sealed record DailySalesData(DateOnly Date, int SaleCount, decimal Total);

    /// <summary>
    /// Completed sales in a range; Days are the shop's local calendar days (FIX-12). Every completed sale of the range is read, so IsTruncated
    /// is always false since FIX-12 (kept for callers written against the earlier 2000-sale scan window).
    /// </summary>
    public sealed record SalesReportData(
        DateTime From, DateTime To, int SaleCount, decimal GrandTotal, decimal AverageSale, IReadOnlyList<DailySalesData> Days, bool IsTruncated);

    public sealed record InventorySnapshotData(int StockItemCount, int ProductCount, decimal TotalOnHand, int OutOfStockCount);

    public sealed record PurchasingOverviewData(
        int TotalOrders, int Draft, int Submitted, int Received, int Cancelled, decimal ReceivedValue, decimal OpenValue);

    /// <summary>Counts of customers or suppliers.</summary>
    public sealed record PartySummaryData(int Total, int Active, int Inactive);

    /// <summary>Every section is reported independently; a missing module only makes its own section unavailable.</summary>
    public sealed record BusinessOverviewResult(
        DateTime From,
        DateTime To,
        ReportResult<SalesReportData> Sales,
        ReportResult<InventorySnapshotData> Inventory,
        ReportResult<PurchasingOverviewData> Purchasing,
        ReportResult<PartySummaryData> Customers,
        ReportResult<PartySummaryData> Suppliers);
}

namespace Reporting.Contracts.Interfaces
{
    using Reporting.Contracts.Models;

    /// <summary>
    /// Read-only business reports built from the other modules' read contracts. Implemented by Reporting.Infrastructure.Services.ReportProvider.
    /// Reporting owns no data: nothing here is stored.
    /// </summary>
    public interface IReportProvider
    {
        /// <summary>Completed sales between from and to (inclusive, at most 366 days).</summary>
        Task<ReportResult<SalesReportData>> GetSalesReportAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default);

        Task<ReportResult<InventorySnapshotData>> GetInventorySnapshotAsync(CancellationToken cancellationToken = default);

        Task<ReportResult<PurchasingOverviewData>> GetPurchasingOverviewAsync(CancellationToken cancellationToken = default);

        Task<ReportResult<PartySummaryData>> GetCustomerSummaryAsync(CancellationToken cancellationToken = default);

        Task<ReportResult<PartySummaryData>> GetSupplierSummaryAsync(CancellationToken cancellationToken = default);

        Task<BusinessOverviewResult> GetBusinessOverviewAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default);
    }
}
