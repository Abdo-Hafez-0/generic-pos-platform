using System.Globalization;
using Platform.Application.Abstractions.Authorization;
using Platform.Application.Abstractions.Data;
using Catalog.Contracts.Interfaces;
using Catalog.Contracts.Models;
using Inventory.Contracts.Interfaces;
using Platform.Core.Results;
using Purchasing.Application.Abstractions;
using Purchasing.Application.Repositories;
using Purchasing.Domain.Entities;
using Purchasing.Domain.ValueObjects;
using Suppliers.Contracts.Interfaces;
using Suppliers.Contracts.Models;

namespace Purchasing.Application.Commands;

internal static class OrderLoader
{
    public static async Task<(PurchaseOrder? Order, Error? Error)> LoadAsync(
        IPurchaseOrderRepository repository, Guid orderId, string use, CancellationToken cancellationToken)
    {
        var order = await repository.GetByIdAsync(new PurchaseOrderId(orderId), cancellationToken);
        return order is null
            ? (null, Error.NotFound($"Purchasing.{use}.OrderNotFound", $"Purchase order '{orderId}' was not found."))
            : (order, null);
    }
}

// ============================================================
// CreatePurchaseOrder
// ============================================================

public sealed record CreatePurchaseOrderCommand(Guid SupplierId, string? Reference = null, string? Notes = null);

/// <summary>Creates a Draft order for an ACTIVE supplier. The supplier is identified through Suppliers.Contracts only.</summary>
public sealed class CreatePurchaseOrderCommandHandler(
    IPurchaseOrderRepository repository,
    ISupplierLookup supplierLookup,
    IPurchasingUnitOfWork unitOfWork,
    IAuthorizationService authorization)
{
    public async Task<Result<Guid>> HandleAsync(CreatePurchaseOrderCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(Purchasing.Application.Security.PurchasingCapabilities.EditOrder, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<Guid>(allowed.Error);

        var supplier = await supplierLookup.FindByIdAsync(command.SupplierId, cancellationToken);
        if (supplier is null)
            return Result.Failure<Guid>(Error.NotFound("Purchasing.CreatePurchaseOrder.SupplierNotFound", $"Supplier '{command.SupplierId}' was not found."));

        if (supplier.Status != SupplierStatusContract.Active)
            return Result.Failure<Guid>(Error.Conflict("Purchasing.CreatePurchaseOrder.SupplierInactive", $"Supplier '{supplier.Name}' is not active."));

        var created = PurchaseOrder.Create(supplier.SupplierId, supplier.Code, supplier.Name, command.Reference, command.Notes);
        if (created.IsFailure) return Result.Failure<Guid>(created.Error);

        await repository.AddAsync(created.Value, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success(created.Value.Id.Value);
    }
}

// ============================================================
// AddPurchaseOrderLine
// ============================================================

/// <summary>
/// Adds a product line. The product is found by SKU (or by ID when the code is a GUID) through Catalog.Contracts. When no unit cost
/// is supplied the Catalog cost price is used if it has one. SKU, name and cost are snapshotted on the line.
/// </summary>
public sealed record AddPurchaseOrderLineCommand(Guid OrderId, string ProductCode, decimal Quantity, decimal? UnitCost = null);

public sealed class AddPurchaseOrderLineCommandHandler(
    IPurchaseOrderRepository repository,
    IProductLookup productLookup,
    IPurchasingUnitOfWork unitOfWork,
    IAuthorizationService authorization)
{
    public async Task<Result<Guid>> HandleAsync(AddPurchaseOrderLineCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(Purchasing.Application.Security.PurchasingCapabilities.EditOrder, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<Guid>(allowed.Error);

        var (order, error) = await OrderLoader.LoadAsync(repository, command.OrderId, "AddLine", cancellationToken);
        if (order is null) return Result.Failure<Guid>(error!);

        if (string.IsNullOrWhiteSpace(command.ProductCode))
            return Result.Failure<Guid>(Error.Validation("Purchasing.AddLine.ProductCodeRequired", "A product SKU (or ID) is required."));

        var quantity = OrderQuantity.Create(command.Quantity);
        if (quantity.IsFailure) return Result.Failure<Guid>(quantity.Error);

        var code = command.ProductCode.Trim();
        ProductLookupResult? product = Guid.TryParse(code, out var productId)
            ? await productLookup.FindByIdAsync(productId, cancellationToken)
            : null;
        product ??= await productLookup.FindBySkuAsync(code, cancellationToken);

        if (product is null)
            return Result.Failure<Guid>(Error.NotFound("Purchasing.AddLine.ProductNotFound", $"No product found for '{code}'."));
        if (product.Status != ProductStatusContract.Active)
            return Result.Failure<Guid>(Error.Conflict("Purchasing.AddLine.ProductInactive", $"Product '{product.Name}' is not active."));

        var unitCost = command.UnitCost ?? product.CostPrice;
        if (unitCost is null)
            return Result.Failure<Guid>(Error.Validation("Purchasing.AddLine.CostRequired", $"A unit cost is required: product '{product.Name}' has no cost price."));

        var cost = Money.Create(unitCost.Value);
        if (cost.IsFailure) return Result.Failure<Guid>(cost.Error);

        var added = order.AddLine(product.ProductId, product.Sku, product.Name, quantity.Value, cost.Value);
        if (added.IsFailure) return Result.Failure<Guid>(added.Error);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success(added.Value.Id.Value);
    }
}

// ============================================================
// RemovePurchaseOrderLine / ChangePurchaseOrderLineQuantity
// ============================================================

public sealed record RemovePurchaseOrderLineCommand(Guid OrderId, Guid LineId);

public sealed class RemovePurchaseOrderLineCommandHandler(IPurchaseOrderRepository repository, IPurchasingUnitOfWork unitOfWork, IAuthorizationService authorization)
{
    public async Task<Result> HandleAsync(RemovePurchaseOrderLineCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(Purchasing.Application.Security.PurchasingCapabilities.EditOrder, cancellationToken);
        if (allowed.IsFailure) return allowed;

        var (order, error) = await OrderLoader.LoadAsync(repository, command.OrderId, "RemoveLine", cancellationToken);
        if (order is null) return Result.Failure(error!);

        var result = order.RemoveLine(new PurchaseOrderLineId(command.LineId));
        if (result.IsFailure) return result;

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

public sealed record ChangePurchaseOrderLineQuantityCommand(Guid OrderId, Guid LineId, decimal Quantity);

public sealed class ChangePurchaseOrderLineQuantityCommandHandler(IPurchaseOrderRepository repository, IPurchasingUnitOfWork unitOfWork, IAuthorizationService authorization)
{
    public async Task<Result> HandleAsync(ChangePurchaseOrderLineQuantityCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(Purchasing.Application.Security.PurchasingCapabilities.EditOrder, cancellationToken);
        if (allowed.IsFailure) return allowed;

        var quantity = OrderQuantity.Create(command.Quantity);
        if (quantity.IsFailure) return Result.Failure(quantity.Error);

        var (order, error) = await OrderLoader.LoadAsync(repository, command.OrderId, "ChangeQuantity", cancellationToken);
        if (order is null) return Result.Failure(error!);

        var result = order.ChangeLineQuantity(new PurchaseOrderLineId(command.LineId), quantity.Value);
        if (result.IsFailure) return result;

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

// ============================================================
// Submit / Cancel
// ============================================================

public sealed record SubmitPurchaseOrderCommand(Guid OrderId);

public sealed class SubmitPurchaseOrderCommandHandler(IPurchaseOrderRepository repository, IPurchasingUnitOfWork unitOfWork, IAuthorizationService authorization)
{
    public async Task<Result> HandleAsync(SubmitPurchaseOrderCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(Purchasing.Application.Security.PurchasingCapabilities.SubmitOrder, cancellationToken);
        if (allowed.IsFailure) return allowed;

        var (order, error) = await OrderLoader.LoadAsync(repository, command.OrderId, "Submit", cancellationToken);
        if (order is null) return Result.Failure(error!);

        var result = order.Submit();
        if (result.IsFailure) return result;

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

public sealed record CancelPurchaseOrderCommand(Guid OrderId, string Reason);

public sealed class CancelPurchaseOrderCommandHandler(IPurchaseOrderRepository repository, IPurchasingUnitOfWork unitOfWork, IAuthorizationService authorization)
{
    public async Task<Result> HandleAsync(CancelPurchaseOrderCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(Purchasing.Application.Security.PurchasingCapabilities.CancelOrder, cancellationToken);
        if (allowed.IsFailure) return allowed;

        var (order, error) = await OrderLoader.LoadAsync(repository, command.OrderId, "Cancel", cancellationToken);
        if (order is null) return Result.Failure(error!);

        var result = order.Cancel(command.Reason);
        if (result.IsFailure) return result;

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

// ============================================================
// ClosePurchaseOrderShort (FIX-09)
// ============================================================

/// <summary>Closes a partly received order short: what arrived stays in stock, the rest is no longer expected. A reason is required.</summary>
public sealed record ClosePurchaseOrderShortCommand(Guid OrderId, string Reason);

public sealed class ClosePurchaseOrderShortCommandHandler(IPurchaseOrderRepository repository, IPurchasingUnitOfWork unitOfWork, IAuthorizationService authorization)
{
    public async Task<Result> HandleAsync(ClosePurchaseOrderShortCommand command, CancellationToken cancellationToken = default)
    {
        // closing short ends what is still expected, like cancelling does for an order with nothing received
        var allowed = await authorization.AuthorizeAsync(Purchasing.Application.Security.PurchasingCapabilities.CancelOrder, cancellationToken);
        if (allowed.IsFailure) return allowed;

        var (order, error) = await OrderLoader.LoadAsync(repository, command.OrderId, "CloseShort", cancellationToken);
        if (order is null) return Result.Failure(error!);

        var result = order.CloseShort(command.Reason);
        if (result.IsFailure) return result;

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

// ============================================================
// ReceivePurchaseOrder
// ============================================================

/// <summary>How much of one order line arrived in this delivery (FIX-09). Zero means "nothing of this line this time".</summary>
public sealed record ReceiveLineQuantity(Guid LineId, decimal Quantity);

/// <summary>
/// Receives a delivery of a placed order into a warehouse, line by line, through Inventory.Contracts (IStockReceiptService).
///
/// FIX-09: <see cref="Lines"/> says how much of each line arrived (never more than is still outstanding); null receives everything still
/// outstanding. The order becomes PartiallyReceived until every unit has arrived (or it is closed short), then Received.
///
/// CONSISTENCY (Stage 12): in the desktop the whole delivery - every line in Inventory and the order's progress - is ONE transaction
/// (IAtomicOperation): it commits completely or not at all. Only hosts without IAtomicOperation (unit-test hosts) use the fallback below,
/// where each line is received in Inventory first and then saved on the order immediately (the progress made is kept); receiving
/// "everything outstanding" again then continues with what is left and never receives a unit twice. The stock movement reference
/// carries the order number and line ID.
/// </summary>
public sealed record ReceivePurchaseOrderCommand(Guid OrderId, Guid WarehouseId, IReadOnlyList<ReceiveLineQuantity>? Lines = null);

public sealed class ReceivePurchaseOrderCommandHandler(
    IPurchaseOrderRepository repository,
    IStockReceiptService stockReceipts,
    IPurchasingUnitOfWork unitOfWork,
    IAuthorizationService authorization,
    IAtomicOperation? atomicOperation = null,
    Platform.Application.Abstractions.Auditing.IBusinessEventSink? businessEvents = null)
{
    public async Task<Result<int>> HandleAsync(ReceivePurchaseOrderCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(Purchasing.Application.Security.PurchasingCapabilities.ReceiveOrder, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<int>(allowed.Error);

        if (command.Lines is { } requested)
        {
            if (requested.Any(l => l.Quantity < 0m))
                return Result.Failure<int>(Error.Validation("Purchasing.Receive.InvalidQuantity", "A received quantity cannot be negative."));
            if (requested.GroupBy(l => l.LineId).Any(g => g.Count() > 1))
                return Result.Failure<int>(Error.Validation("Purchasing.Receive.DuplicateLine", "Each order line can appear only once in a delivery."));
            if (requested.All(l => l.Quantity == 0m))
                return Result.Failure<int>(Error.Validation("Purchasing.Receive.NothingToReceive", "Enter how much of at least one line arrived."));
        }

        // With a transaction the whole delivery is all-or-nothing: every line's stock and the order's progress are committed together
        // or not at all. Without one (unit-test hosts) progress is kept after every line.
        var received = atomicOperation is null
            ? await ReceiveAsync(command, transactional: false, cancellationToken)
            : await atomicOperation.ExecuteAsync(() => ReceiveAsync(command, transactional: true, cancellationToken), cancellationToken);

        // FIX-05: the audit log, after the commit (best effort)
        if (received.IsSuccess)
            await Platform.Application.Abstractions.Auditing.BusinessEventSinkExtensions.TryRecordAsync(businessEvents,
                Platform.Application.Abstractions.Auditing.BusinessEvent.Create("purchasing", "purchase-order.received", "purchase-order", command.OrderId.ToString(),
                    $"{received.Value} line(s) received into stock.", $"warehouse={command.WarehouseId}"));
        return received;
    }

    private async Task<Result<int>> ReceiveAsync(ReceivePurchaseOrderCommand command, bool transactional, CancellationToken cancellationToken)
    {
        var (order, error) = await OrderLoader.LoadAsync(repository, command.OrderId, "Receive", cancellationToken);
        if (order is null) return Result.Failure<int>(error!);

        var begin = order.BeginReceiving(command.WarehouseId);
        if (begin.IsFailure) return Result.Failure<int>(begin.Error);

        // what arrives now, checked against the order BEFORE any stock moves (so a refused delivery changes nothing, with or without a transaction)
        var delivery = new List<(PurchaseOrderLine Line, OrderQuantity Quantity)>();
        if (command.Lines is null)
        {
            foreach (var line in order.Lines.Where(l => !l.IsReceived))
                delivery.Add((line, new OrderQuantity(line.OutstandingQuantity)));
        }
        else
        {
            foreach (var requested in command.Lines.Where(l => l.Quantity > 0m))
            {
                var line = order.FindLine(new PurchaseOrderLineId(requested.LineId));
                if (line is null)
                    return Result.Failure<int>(Error.NotFound("Purchasing.PurchaseOrder.LineNotFound", "The line was not found on this order."));
                if (requested.Quantity > line.OutstandingQuantity)
                    return Result.Failure<int>(line.IsReceived
                        ? Error.Conflict("Purchasing.PurchaseOrder.LineAlreadyReceived", $"'{line.ProductName}' was already received in full.")
                        : Error.Conflict("Purchasing.PurchaseOrder.MoreThanOrdered",
                            $"Only {line.OutstandingQuantity.ToString("0.###", CultureInfo.CurrentCulture)} of '{line.ProductName}' are still expected; " +
                            $"{requested.Quantity.ToString("0.###", CultureInfo.CurrentCulture)} cannot be received."));
                delivery.Add((line, new OrderQuantity(requested.Quantity)));
            }
        }

        if (delivery.Count == 0)
            return Result.Failure<int>(Error.Conflict("Purchasing.Receive.NothingOutstanding", "Every line of this order was already received."));

        var receivedNow = 0;
        foreach (var (line, quantity) in delivery)
        {
            var receipt = await stockReceipts.ReceiveStockAsync(
                line.ProductId, command.WarehouseId, quantity.Value,
                $"{order.Number} line {line.Id.Value:N}", cancellationToken);

            if (!receipt.IsSuccess)
            {
                if (transactional)
                    return Result.Failure<int>(Error.Failure("Purchasing.Receive.StockReceiptFailed",
                        $"Could not receive '{line.ProductName}': [{receipt.ErrorCode}] {receipt.ErrorMessage} Nothing was received: no stock was changed and the order is unchanged."));

                order.UpdateReceivingStatus();
                await unitOfWork.SaveChangesAsync(cancellationToken);   // keep the progress made so far
                return Result.Failure<int>(Error.Failure("Purchasing.Receive.StockReceiptFailed",
                    $"Could not receive '{line.ProductName}': [{receipt.ErrorCode}] {receipt.ErrorMessage} " +
                    $"{receivedNow} of {delivery.Count} line(s) of this delivery are received; receive the rest again to continue."));
            }

            var recorded = order.ReceiveLine(line.Id, quantity);
            if (recorded.IsFailure) return Result.Failure<int>(recorded.Error);   // cannot happen: checked above
            receivedNow++;
            await unitOfWork.SaveChangesAsync(cancellationToken);       // durable progress after every line (inside the transaction when there is one)
        }

        var status = order.UpdateReceivingStatus();
        if (status.IsFailure) return Result.Failure<int>(status.Error);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success(receivedNow);
    }
}
