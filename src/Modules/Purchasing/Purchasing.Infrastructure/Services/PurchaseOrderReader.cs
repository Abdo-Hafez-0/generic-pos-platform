using Purchasing.Application.Repositories;
using Purchasing.Contracts.Interfaces;
using Purchasing.Contracts.Models;
using Purchasing.Domain.Entities;
using Purchasing.Domain.Enums;
using Purchasing.Domain.ValueObjects;

namespace Purchasing.Infrastructure.Services;

/// <summary>Implements IPurchaseOrderReader from Purchasing.Contracts (read-only).</summary>
internal sealed class PurchaseOrderReader(IPurchaseOrderRepository repository) : IPurchaseOrderReader
{
    public async Task<PurchaseOrderResult?> GetAsync(Guid orderId, CancellationToken cancellationToken = default)
        => (await repository.GetByIdAsync(new PurchaseOrderId(orderId), cancellationToken)) is { } o ? ToResult(o, withLines: true) : null;

    public async Task<IReadOnlyList<PurchaseOrderResult>> ListRecentAsync(
        int limit = 50, PurchaseOrderStatusContract? status = null, CancellationToken cancellationToken = default)
    {
        PurchaseOrderStatus? domainStatus = status is null ? null : (PurchaseOrderStatus)(int)status.Value;
        var orders = await repository.ListAsync(0, Math.Clamp(limit, 1, 1000), domainStatus, cancellationToken);
        return orders.Select(o => ToResult(o, withLines: false)).ToList();
    }

    public async Task<PurchaseSummaryResult> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        var rows = await repository.GetStatusTotalsAsync(cancellationToken);

        int Count(PurchaseOrderStatus s) => rows.Count(r => r.Status == s);

        return new PurchaseSummaryResult(
            rows.Count,
            Count(PurchaseOrderStatus.Draft), Count(PurchaseOrderStatus.Submitted), Count(PurchaseOrderStatus.Received), Count(PurchaseOrderStatus.Cancelled),
            ReceivedValue: rows.Sum(r => r.Received),
            OpenValue: rows.Where(r => r.Status is PurchaseOrderStatus.Draft or PurchaseOrderStatus.Submitted or PurchaseOrderStatus.PartiallyReceived)
                .Sum(r => r.Total - r.Received),
            PartiallyReceived: Count(PurchaseOrderStatus.PartiallyReceived),
            Closed: Count(PurchaseOrderStatus.Closed));
    }

    private static PurchaseOrderResult ToResult(PurchaseOrder o, bool withLines) => new(
        o.Id.Value, o.Number, o.SupplierId, o.SupplierName, (PurchaseOrderStatusContract)(int)o.Status, o.TotalAmount.Amount,
        o.WarehouseId, o.CreatedAt, o.ReceivedAt,
        withLines
            ? o.Lines.Select(l => new PurchaseOrderLineResult(
                l.Id.Value, l.ProductId, l.ProductSku, l.ProductName, l.Quantity.Value, l.UnitCost.Amount, l.LineTotal.Amount, l.IsReceived,
                l.ReceivedQuantity, l.ReturnedQuantity)).ToList()
            : []);
}
