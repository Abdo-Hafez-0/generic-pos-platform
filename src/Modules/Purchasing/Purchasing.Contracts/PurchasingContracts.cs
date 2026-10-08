namespace Purchasing.Contracts.Models
{
    public enum PurchaseOrderStatusContract
    {
        Draft = 1,
        Submitted = 2,
        Received = 3,
        Cancelled = 4,

        /// <summary>Some goods were received; the rest is still expected (FIX-09).</summary>
        PartiallyReceived = 5,

        /// <summary>Closed short: part was received and the rest will not come (FIX-09).</summary>
        Closed = 6
    }

    /// <summary>One order line; IsReceived means the whole quantity arrived, ReceivedQuantity adds up the deliveries so far (FIX-09).</summary>
    public sealed record PurchaseOrderLineResult(
        Guid LineId, Guid ProductId, string ProductSku, string ProductName, decimal Quantity, decimal UnitCost, decimal LineTotal, bool IsReceived,
        decimal ReceivedQuantity = 0m);

    /// <summary>Read model of a purchase order. Never exposes Purchasing.Domain types.</summary>
    public sealed record PurchaseOrderResult(
        Guid OrderId,
        string Number,
        Guid SupplierId,
        string SupplierName,
        PurchaseOrderStatusContract Status,
        decimal TotalAmount,
        Guid? WarehouseId,
        DateTime CreatedAt,
        DateTime? ReceivedAt,
        IReadOnlyList<PurchaseOrderLineResult> Lines);

    /// <summary>
    /// Read model used by reporting. ReceivedValue = goods received so far (every delivery, at the order's unit costs); OpenValue = what
    /// is still to receive on drafts and orders awaiting goods. PartiallyReceived and Closed (FIX-09) are counted apart from Submitted and Received.
    /// </summary>
    public sealed record PurchaseSummaryResult(
        int TotalOrders, int Draft, int Submitted, int Received, int Cancelled, decimal ReceivedValue, decimal OpenValue,
        int PartiallyReceived = 0, int Closed = 0);
}

namespace Purchasing.Contracts.Interfaces
{
    using Purchasing.Contracts.Models;

    /// <summary>
    /// Read-only access to purchase orders for other modules (reporting). Implemented by Purchasing.Infrastructure.Services.PurchaseOrderReader.
    /// </summary>
    public interface IPurchaseOrderReader
    {
        Task<PurchaseOrderResult?> GetAsync(Guid orderId, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<PurchaseOrderResult>> ListRecentAsync(
            int limit = 50, PurchaseOrderStatusContract? status = null, CancellationToken cancellationToken = default);

        Task<PurchaseSummaryResult> GetSummaryAsync(CancellationToken cancellationToken = default);
    }
}
