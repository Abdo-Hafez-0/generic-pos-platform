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
            l.Id.Value, l.ProductId, l.ProductSku, l.ProductName, l.Quantity.Value, l.UnitCost.Amount, l.LineTotal.Amount, l.IsReceived,
            l.ReceivedQuantity, o.IsAwaitingGoods ? l.OutstandingQuantity : 0m)).ToList(),
        o.ReceivedAmount.Amount, o.ClosedAt, o.ClosingReason);

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

// ---- for the purchase orders screen (FIX-01d)

/// <summary>A supplier an order can be placed with.</summary>
public sealed record OrderSupplierDto(Guid SupplierId, string Code, string Name);

/// <summary>ACTIVE suppliers whose code, name, e-mail or phone contains the text (through Suppliers.Contracts), ordered by name.</summary>
public sealed record FindOrderSuppliersQuery(string Text);

public sealed class FindOrderSuppliersQueryHandler(Suppliers.Contracts.Interfaces.ISupplierReader suppliers)
{
    public async Task<IReadOnlyList<OrderSupplierDto>> HandleAsync(FindOrderSuppliersQuery query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query.Text)) return [];

        var found = await suppliers.SearchAsync(query.Text.Trim(), 50, cancellationToken);
        return found
            .Where(s => s.Status == Suppliers.Contracts.Models.SupplierStatusContract.Active)
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(s => new OrderSupplierDto(s.SupplierId, s.Code, s.Name))
            .ToList();
    }
}

/// <summary>A warehouse an order can be received into.</summary>
public sealed record ReceivingWarehouseDto(Guid WarehouseId, string Code, string Name);

/// <summary>The active warehouses (through Inventory.Contracts), ordered by name.</summary>
public sealed record ListReceivingWarehousesQuery;

public sealed class ListReceivingWarehousesQueryHandler(Inventory.Contracts.Interfaces.IInventoryReader inventory)
{
    public async Task<IReadOnlyList<ReceivingWarehouseDto>> HandleAsync(ListReceivingWarehousesQuery query, CancellationToken cancellationToken = default)
        => (await inventory.GetAllWarehousesAsync(cancellationToken))
            .Where(w => w.IsActive)
            .OrderBy(w => w.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(w => new ReceivingWarehouseDto(w.WarehouseId, w.Code, w.Name))
            .ToList();
}
