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
// ReceivePurchaseOrder
// ============================================================

/// <summary>
/// Receives a Submitted order into a warehouse, line by line, through Inventory.Contracts (IStockReceiptService).
///
/// CONSISTENCY: Purchasing and Inventory have separate databases contexts, so there is no cross-module transaction. Each line is
/// received in Inventory first, then recorded as received on the order and SAVED immediately. If a line fails, the order stays
/// Submitted with the lines received so far marked; calling Receive again continues with the remaining lines and never receives a
/// line twice. (The only unrecoverable window - a crash after Inventory accepted a line but before the order was saved - would
/// duplicate that single line on retry; the stock movement reference carries the order number and line ID so it can be audited.)
/// </summary>
public sealed record ReceivePurchaseOrderCommand(Guid OrderId, Guid WarehouseId);

public sealed class ReceivePurchaseOrderCommandHandler(
    IPurchaseOrderRepository repository,
    IStockReceiptService stockReceipts,
    IPurchasingUnitOfWork unitOfWork,
    IAuthorizationService authorization,
    IAtomicOperation? atomicOperation = null)
{
    public async Task<Result<int>> HandleAsync(ReceivePurchaseOrderCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(Purchasing.Application.Security.PurchasingCapabilities.ReceiveOrder, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<int>(allowed.Error);

        // With a transaction the whole receipt is all-or-nothing: every line's stock and the order's progress are committed together
        // or not at all. Without one (unit-test hosts) receiving is resumable: progress is kept after every line.
        return atomicOperation is null
            ? await ReceiveAsync(command, transactional: false, cancellationToken)
            : await atomicOperation.ExecuteAsync(() => ReceiveAsync(command, transactional: true, cancellationToken), cancellationToken);
    }

    private async Task<Result<int>> ReceiveAsync(ReceivePurchaseOrderCommand command, bool transactional, CancellationToken cancellationToken)
    {
        var (order, error) = await OrderLoader.LoadAsync(repository, command.OrderId, "Receive", cancellationToken);
        if (order is null) return Result.Failure<int>(error!);

        var begin = order.BeginReceiving(command.WarehouseId);
        if (begin.IsFailure) return Result.Failure<int>(begin.Error);

        var receivedNow = 0;
        foreach (var line in order.Lines.Where(l => !l.IsReceived).ToList())
        {
            var receipt = await stockReceipts.ReceiveStockAsync(
                line.ProductId, command.WarehouseId, line.Quantity.Value,
                $"{order.Number} line {line.Id.Value:N}", cancellationToken);

            if (!receipt.IsSuccess)
            {
                if (transactional)
                    return Result.Failure<int>(Error.Failure("Purchasing.Receive.StockReceiptFailed",
                        $"Could not receive '{line.ProductName}': [{receipt.ErrorCode}] {receipt.ErrorMessage} Nothing was received: no stock was changed and the order is unchanged."));

                await unitOfWork.SaveChangesAsync(cancellationToken);   // keep the progress made so far
                var outstanding = order.Lines.Count(l => !l.IsReceived);
                return Result.Failure<int>(Error.Failure("Purchasing.Receive.StockReceiptFailed",
                    $"Could not receive '{line.ProductName}': [{receipt.ErrorCode}] {receipt.ErrorMessage} " +
                    $"{order.Lines.Count - outstanding} of {order.Lines.Count} line(s) are received; receive again to continue."));
            }

            order.MarkLineReceived(line.Id);
            receivedNow++;
            await unitOfWork.SaveChangesAsync(cancellationToken);       // durable progress after every line
        }

        var complete = order.CompleteIfFullyReceived();
        if (complete.IsFailure) return Result.Failure<int>(complete.Error);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success(receivedNow);
    }
}
