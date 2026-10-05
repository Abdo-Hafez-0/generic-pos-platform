namespace Purchasing.Contracts.Models
{
    public enum PurchaseOrderStatusContract
    {
        Draft = 1,
        Submitted = 2,
        Received = 3,
        Cancelled = 4
    }

    public sealed record PurchaseOrderLineResult(
        Guid LineId, Guid ProductId, string ProductSku, string ProductName, decimal Quantity, decimal UnitCost, decimal LineTotal, bool IsReceived);

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

    /// <summary>Read model used by reporting.</summary>
    public sealed record PurchaseSummaryResult(
        int TotalOrders, int Draft, int Submitted, int Received, int Cancelled, decimal ReceivedValue, decimal OpenValue);
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
