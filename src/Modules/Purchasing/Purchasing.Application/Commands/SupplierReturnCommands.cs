using Inventory.Contracts.Interfaces;
using Platform.Application.Abstractions.Auditing;
using Platform.Application.Abstractions.Authorization;
using Platform.Application.Abstractions.Data;
using Platform.Core.Results;
using Purchasing.Application.Abstractions;
using Purchasing.Application.Repositories;
using Purchasing.Domain.Entities;
using Purchasing.Domain.ValueObjects;

namespace Purchasing.Application.Commands;

// ============================================================
// ReturnToSupplier (FIX-09b)
// ============================================================

/// <summary>How much of one order line goes back to the supplier.</summary>
public sealed record ReturnLineQuantity(Guid LineId, decimal Quantity);

/// <summary>
/// Sends received goods of a purchase order back to its supplier: each line never more than was received minus earlier returns, out of the
/// warehouse the order was received into, at the order's unit cost, with a reason. The stock leaves through Inventory.Contracts
/// (IStockIssueService; refused when the warehouse no longer holds enough).
///
/// CONSISTENCY: in the desktop the whole return - every line's stock in Inventory, the order's returned quantities and the return record -
/// is ONE transaction (IAtomicOperation, Stage 12 rule ARCH-RES-004). Only hosts without it (unit-test hosts) keep the lines that left
/// stock before a failure: the return then records exactly those lines.
/// </summary>
public sealed record ReturnToSupplierCommand(Guid OrderId, string Reason, IReadOnlyList<ReturnLineQuantity> Lines);

public sealed class ReturnToSupplierCommandHandler(
    IPurchaseOrderRepository orders,
    ISupplierReturnRepository returns,
    IStockIssueService stockIssues,
    IPurchasingUnitOfWork unitOfWork,
    IAuthorizationService authorization,
    IAtomicOperation? atomicOperation = null,
    IBusinessEventSink? businessEvents = null)
{
    private sealed record Returned(Guid ReturnId, string Number, string OrderNumber, string Supplier, int Lines, decimal Total, Guid WarehouseId, string Reason);

    public async Task<Result<Guid>> HandleAsync(ReturnToSupplierCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(Purchasing.Application.Security.PurchasingCapabilities.ReturnGoods, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<Guid>(allowed.Error);

        var requested = command.Lines ?? [];
        if (requested.Any(l => l.Quantity < 0m))
            return Result.Failure<Guid>(Error.Validation("Purchasing.SupplierReturn.InvalidQuantity", "A returned quantity cannot be negative."));
        if (requested.GroupBy(l => l.LineId).Any(g => g.Count() > 1))
            return Result.Failure<Guid>(Error.Validation("Purchasing.SupplierReturn.DuplicateLine", "Each order line can appear only once in a return."));
        if (requested.All(l => l.Quantity == 0m))
            return Result.Failure<Guid>(Error.Validation("Purchasing.SupplierReturn.NothingToReturn", "Enter how much of at least one line goes back to the supplier."));

        var done = atomicOperation is null
            ? await ReturnAsync(command, transactional: false, cancellationToken)
            : await atomicOperation.ExecuteAsync(() => ReturnAsync(command, transactional: true, cancellationToken), cancellationToken);
        if (done.IsFailure) return Result.Failure<Guid>(done.Error);

        // FIX-05: the audit log, after the commit (best effort)
        var r = done.Value;
        await businessEvents.TryRecordAsync(BusinessEvent.Create("purchasing", "supplier-return.created", "supplier-return", r.ReturnId.ToString(),
            $"Return {r.Number}: {r.Lines} line(s) worth {r.Total:0.00} sent back to {r.Supplier} from order {r.OrderNumber}. Reason: {r.Reason}",
            $"order={command.OrderId}; warehouse={r.WarehouseId}"));
        return Result.Success(r.ReturnId);
    }

    private async Task<Result<Returned>> ReturnAsync(ReturnToSupplierCommand command, bool transactional, CancellationToken cancellationToken)
    {
        var (order, error) = await OrderLoader.LoadAsync(orders, command.OrderId, "SupplierReturn", cancellationToken);
        if (order is null) return Result.Failure<Returned>(error!);

        var started = SupplierReturn.Start(order, command.Reason);
        if (started.IsFailure) return Result.Failure<Returned>(started.Error);
        var supplierReturn = started.Value;

        // every line checked against the order BEFORE any stock moves
        var wanted = new List<(PurchaseOrderLine Line, OrderQuantity Quantity)>();
        foreach (var requested in command.Lines.Where(l => l.Quantity > 0m))
        {
            var line = order.FindLine(new PurchaseOrderLineId(requested.LineId));
            if (line is null)
                return Result.Failure<Returned>(Error.NotFound("Purchasing.PurchaseOrder.LineNotFound", "The line was not found on this order."));
            if (requested.Quantity > line.ReturnableQuantity)
            {
                var refused = order.RecordReturn(supplierReturn, line.Id, new OrderQuantity(requested.Quantity));   // the domain's plain refusal; changes nothing
                return Result.Failure<Returned>(refused.Error);
            }
            wanted.Add((line, new OrderQuantity(requested.Quantity)));
        }

        await returns.AddAsync(supplierReturn, cancellationToken);
        foreach (var (line, quantity) in wanted)
        {
            var issued = await stockIssues.IssueStockAsync(
                line.ProductId, supplierReturn.WarehouseId, quantity.Value,
                $"{supplierReturn.Number} to supplier, order {order.Number} line {line.Id.Value:N}", cancellationToken);

            if (!issued.IsSuccess)
            {
                if (transactional || supplierReturn.Lines.Count == 0)
                    return Result.Failure<Returned>(Error.Failure("Purchasing.SupplierReturn.StockIssueFailed",
                        $"Could not take '{line.ProductName}' out of stock: {issued.ErrorMessage} Nothing was returned: no stock was changed and the order is unchanged."));

                await unitOfWork.SaveChangesAsync(cancellationToken);   // without a transaction: the lines that already left stock stay recorded
                return Result.Failure<Returned>(Error.Failure("Purchasing.SupplierReturn.StockIssueFailed",
                    $"Could not take '{line.ProductName}' out of stock: {issued.ErrorMessage} Return {supplierReturn.Number} records the " +
                    $"{supplierReturn.Lines.Count} line(s) that did leave stock."));
            }

            var recorded = order.RecordReturn(supplierReturn, line.Id, quantity);
            if (recorded.IsFailure) return Result.Failure<Returned>(recorded.Error);   // cannot happen: checked above
            await unitOfWork.SaveChangesAsync(cancellationToken);   // inside the transaction when there is one
        }

        return Result.Success(new Returned(supplierReturn.Id.Value, supplierReturn.Number, order.Number, order.SupplierName,
            supplierReturn.Lines.Count, supplierReturn.TotalAmount.Amount, supplierReturn.WarehouseId, supplierReturn.Reason));
    }
}
