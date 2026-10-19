using Platform.Core.Results;
using Sales.Application.Abstractions;
using Sales.Application.Repositories;
using Sales.Domain.Entities;
using Sales.Domain.Enums;
using Sales.Domain.ValueObjects;

namespace Sales.Application.Commands;

/// <summary>MISS-10: one line to return - the sale line, the units, and whether they go back into stock (false = damaged).</summary>
public sealed record ReturnLine(Guid SaleItemId, decimal Quantity, bool BackToStock);

/// <summary>
/// MISS-10: records a return against a COMPLETED sale (owner's decisions 2026-10-19: only against a recorded sale, a reason is required).
///
/// RULES
///   - Never more units of a line than were sold minus what earlier returns of the same sale took back.
///   - The refund of a line is its share of what the customer paid for it (the line total: after its discount, tax included), rounded to
///     2 decimals; the units that empty a line take exactly what is left of its total, so a line is never refunded more than it cost and
///     the cents always add up.
///   - The return is recorded as Processed at once (the money and the stock move in the same transaction, by POS).
/// Stock, cash and payments are NOT touched here: POS orchestrates them through their contracts, in one transaction.
/// </summary>
public sealed record RecordReturnCommand(Guid SaleId, IReadOnlyList<ReturnLine> Lines, string Reason);

/// <summary>The recorded return and its lines.</summary>
public sealed record RecordedReturn(Return Return);

public sealed class RecordReturnCommandHandler(
    ISaleRepository saleRepository,
    IReturnRepository returnRepository,
    ISalesUnitOfWork unitOfWork)
{
    public async Task<Result<RecordedReturn>> HandleAsync(RecordReturnCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Reason))
            return Result.Failure<RecordedReturn>(Error.Validation("Sales.Return.ReasonRequired", "Give a reason for the return."));

        var lines = command.Lines.Where(l => l.Quantity != 0m).ToList();
        if (lines.Count == 0)
            return Result.Failure<RecordedReturn>(Error.Validation("Sales.Return.NothingToReturn", "Choose at least one item to return."));

        if (lines.GroupBy(l => l.SaleItemId).Any(g => g.Count() > 1))
            return Result.Failure<RecordedReturn>(Error.Validation("Sales.Return.LineTwice", "Each sale line can appear only once in a return."));

        var sale = await saleRepository.GetByIdAsync(new SaleId(command.SaleId), cancellationToken);
        if (sale is null)
            return Result.Failure<RecordedReturn>(Error.NotFound("Sales.Return.SaleNotFound", $"Sale '{command.SaleId}' was not found."));

        if (sale.Status != SaleStatus.Completed)
            return Result.Failure<RecordedReturn>(Error.Conflict("Sales.Return.SaleNotCompleted", "Only a completed sale can be returned."));

        var earlier = await returnRepository.GetProcessedForSaleAsync(sale.Id, cancellationToken);
        var created = Return.Create(sale.Id, command.Reason);
        if (created.IsFailure) return Result.Failure<RecordedReturn>(created.Error);
        var recorded = created.Value;

        foreach (var line in lines)
        {
            var item = sale.Items.FirstOrDefault(i => i.Id.Value == line.SaleItemId);
            if (item is null)
                return Result.Failure<RecordedReturn>(Error.NotFound("Sales.Return.LineNotInSale", "An item to return is not part of this sale."));

            if (line.Quantity < 0m)
                return Result.Failure<RecordedReturn>(Error.Validation("Sales.Return.QuantityInvalid", $"The quantity to return of '{item.ProductName}' must be more than zero."));

            var previous = earlier.SelectMany(r => r.Items).Where(i => i.OriginalSaleItemId == item.Id).ToList();
            var returnedBefore = previous.Sum(i => i.Quantity.Value);
            var refundedBefore = previous.Sum(i => i.RefundAmount.Amount);
            var remaining = item.Quantity.Value - returnedBefore;
            if (line.Quantity > remaining)
                return Result.Failure<RecordedReturn>(Error.Validation("Sales.Return.TooMany",
                    remaining <= 0m
                        ? $"'{item.ProductName}' was already returned in full."
                        : $"Only {remaining:0.###} of '{item.ProductName}' can still be returned."));

            var refund = line.Quantity == remaining
                ? item.LineTotal.Amount - refundedBefore   // the last units take what is left: the cents add up
                : decimal.Round(item.LineTotal.Amount * line.Quantity / item.Quantity.Value, 2, MidpointRounding.AwayFromZero);

            var added = recorded.AddItem(item.Id, item.CatalogProductId, item.ProductName, new SaleQuantity(line.Quantity), item.UnitPrice,
                reason: null, backToStock: line.BackToStock, refundAmount: new Money(refund));
            if (added.IsFailure) return Result.Failure<RecordedReturn>(added.Error);
        }

        var processed = recorded.Process();
        if (processed.IsFailure) return Result.Failure<RecordedReturn>(processed.Error);

        await returnRepository.AddAsync(recorded, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success(new RecordedReturn(recorded));
    }
}
