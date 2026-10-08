using Purchasing.Domain.Entities;
using Purchasing.Domain.Enums;
using Purchasing.Domain.ValueObjects;

namespace Purchasing.Application.Abstractions
{
    /// <summary>Saves changes to the Purchasing module's own persistence (PurchasingDbContext).</summary>
    public interface IPurchasingUnitOfWork
    {
        Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    }
}

namespace Purchasing.Application.Repositories
{
    public interface IPurchaseOrderRepository
    {
        /// <summary>Loads an order including its lines.</summary>
        Task<PurchaseOrder?> GetByIdAsync(PurchaseOrderId id, CancellationToken cancellationToken = default);

        Task AddAsync(PurchaseOrder order, CancellationToken cancellationToken = default);

        /// <summary>Newest first, WITHOUT lines.</summary>
        Task<IReadOnlyList<PurchaseOrder>> ListAsync(int skip, int take, PurchaseOrderStatus? status, CancellationToken cancellationToken = default);

        Task<int> CountAsync(PurchaseOrderStatus? status, CancellationToken cancellationToken = default);

        /// <summary>(status, total, received value) for every order, WITHOUT lines (for summaries).</summary>
        Task<IReadOnlyList<(PurchaseOrderStatus Status, decimal Total, decimal Received)>> GetStatusTotalsAsync(CancellationToken cancellationToken = default);
    }
}

namespace Purchasing.Application.DTOs
{
    /// <summary>One order line; <see cref="ReceivedQuantity"/> adds up every delivery so far, <see cref="OutstandingQuantity"/> is what is still expected.</summary>
    public sealed record PurchaseOrderLineDto(
        Guid LineId, Guid ProductId, string ProductSku, string ProductName, decimal Quantity, decimal UnitCost, decimal LineTotal, bool IsReceived,
        decimal ReceivedQuantity, decimal OutstandingQuantity);

    public sealed record PurchaseOrderDto(
        Guid OrderId, string Number, Guid SupplierId, string SupplierCode, string SupplierName, string? Reference, string? Notes,
        PurchaseOrderStatus Status, decimal TotalAmount, Guid? WarehouseId, DateTime CreatedAt, DateTime? SubmittedAt, DateTime? ReceivedAt,
        DateTime? CancelledAt, string? CancellationReason, IReadOnlyList<PurchaseOrderLineDto> Lines,
        decimal ReceivedAmount, DateTime? ClosedAt, string? ClosingReason);

    public sealed record PurchaseOrderListItemDto(
        Guid OrderId, string Number, string SupplierName, PurchaseOrderStatus Status, decimal TotalAmount, DateTime CreatedAt);

    public sealed record PurchaseOrderPageDto(IReadOnlyList<PurchaseOrderListItemDto> Items, int Total, int Skip, int Take);
}
