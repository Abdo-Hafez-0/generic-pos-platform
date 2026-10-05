using Purchasing.Application.DTOs;
using Purchasing.Application.Repositories;
using Purchasing.Domain.Entities;
using Purchasing.Domain.Enums;
using Purchasing.Domain.ValueObjects;

namespace Purchasing.Application.Queries;

internal static class PurchaseOrderMapping
{
    public static PurchaseOrderDto ToDto(this PurchaseOrder o) => new(
        o.Id.Value, o.Number, o.SupplierId, o.SupplierCode, o.SupplierName, o.Reference, o.Notes, o.Status, o.TotalAmount.Amount,
        o.WarehouseId, o.CreatedAt, o.SubmittedAt, o.ReceivedAt, o.CancelledAt, o.CancellationReason,
        o.Lines.Select(l => new PurchaseOrderLineDto(
            l.Id.Value, l.ProductId, l.ProductSku, l.ProductName, l.Quantity.Value, l.UnitCost.Amount, l.LineTotal.Amount, l.IsReceived)).ToList());

    public static PurchaseOrderListItemDto ToListItem(this PurchaseOrder o)
        => new(o.Id.Value, o.Number, o.SupplierName, o.Status, o.TotalAmount.Amount, o.CreatedAt);
}

public sealed record GetPurchaseOrderQuery(Guid OrderId);

public sealed class GetPurchaseOrderQueryHandler(IPurchaseOrderRepository repository)
{
    public async Task<PurchaseOrderDto?> HandleAsync(GetPurchaseOrderQuery query, CancellationToken cancellationToken = default)
        => (await repository.GetByIdAsync(new PurchaseOrderId(query.OrderId), cancellationToken))?.ToDto();
}

public sealed record ListPurchaseOrdersQuery(int Skip = 0, int Take = 50, PurchaseOrderStatus? Status = null);

public sealed class ListPurchaseOrdersQueryHandler(IPurchaseOrderRepository repository)
{
    public const int MaxPageSize = 200;

    public async Task<PurchaseOrderPageDto> HandleAsync(ListPurchaseOrdersQuery query, CancellationToken cancellationToken = default)
    {
        var skip = Math.Max(0, query.Skip);
        var take = Math.Clamp(query.Take, 1, MaxPageSize);

        var items = await repository.ListAsync(skip, take, query.Status, cancellationToken);
        var total = await repository.CountAsync(query.Status, cancellationToken);
        return new PurchaseOrderPageDto(items.Select(o => o.ToListItem()).ToList(), total, skip, take);
    }
}
