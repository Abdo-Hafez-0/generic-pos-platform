using System.Globalization;
using CashManagement.Contracts.Interfaces;
using CashManagement.Contracts.Models;
using Inventory.Contracts.Interfaces;
using Payments.Contracts.Interfaces;
using Payments.Contracts.Models;
using Platform.Application.Abstractions.Auditing;
using Platform.Application.Abstractions.Authorization;
using Platform.Application.Abstractions.Data;
using Platform.Application.Abstractions.Hardware;
using Platform.Core.Results;
using POS.Application.Devices;
using POS.Application.Repositories;
using POS.Contracts.Models;
using Sales.Contracts.Interfaces;
using Sales.Contracts.Models;

namespace POS.Application.Commands;

/// <summary>MISS-10: one line to give back - the sale line, how many units, and whether they go back into stock (false = damaged).</summary>
public sealed record RefundLine(Guid SaleItemId, decimal Quantity, bool BackToStock = true);

/// <summary>
/// MISS-10: returns items of a completed sale and gives the money back. <see cref="Method"/> is how the money goes back, chosen by the
/// cashier whatever the customer paid with (owner's decision); "Other" needs <see cref="MethodDetail"/>.
/// </summary>
public sealed record RefundSaleCommand(Guid SaleId, IReadOnlyList<RefundLine> Lines, string Reason, POSPaymentMethod Method, string? MethodDetail = null);

/// <summary>A completed refund: the return, the money given back and how; MISS-10c: what the printer or the drawer could not do.</summary>
public sealed record RefundOutcome(Guid ReturnId, Guid SaleId, decimal RefundTotal, POSPaymentMethod Method, IReadOnlyList<ReturnedLineResult> Lines,
    Guid? RefundPaymentId, IReadOnlyList<POSHardwareNotice>? HardwareNotices = null);

/// <summary>
/// MISS-10 - returns and refunds (owner's decisions 2026-10-19).
///
/// ORCHESTRATION (POS owns the flow; each module owns its data):
///   1. Anyone who can sell may refund (pos.sale.create); a sale older than the shop's limit (PosReturns:DaysAllowed, 14 days) also needs
///      the late-returns permission (pos.return.late).
///   2. Sales records the return against the sale (never more than sold minus earlier returns; the refund is each line's share of what was
///      paid) -> Sales.Contracts ISalesService.RecordReturnAsync.
///   3. Lines going back to stock are received into the warehouse the sale was sold from (its till session's) -> Inventory.Contracts;
///      damaged lines are not.
///   4. The refund is recorded (money paid back, by the method the cashier chose) -> Payments.Contracts (optional module).
///   5. A cash refund leaves this till's drawer shift as a CashRefund movement -> CashManagement.Contracts (optional module); it needs an
///      open shift holding at least the refund.
/// CONSISTENCY: 2-5 run as ONE database transaction (IAtomicOperation): the return, the stock, the refund and the cash movement are saved
/// together or not at all. The audit entry is written after the commit.
/// PERIPHERALS (MISS-10c, after the commit): the RETURN receipt is always printed when a receipt printer is set up (owner's decision), and
/// the drawer opens for a cash refund (PosReceipt:AutoOpenDrawerOnCashSale). A device problem never undoes the refund: it is reported.
/// </summary>
public sealed class RefundSaleCommandHandler(
    ISalesService salesService,
    ISalesReader salesReader,
    IPosCartRepository cartRepository,
    IPosSessionRepository sessionRepository,
    IStockReceiptService stockReceipts,
    IAuthorizationService authorization,
    IPaymentService? paymentService = null,
    ICashMovementRecorder? cashRecorder = null,
    ICashSessionReader? cashShifts = null,
    PosCashOptions? cashOptions = null,
    PosReturnOptions? returnOptions = null,
    TimeProvider? timeProvider = null,
    IAtomicOperation? atomicOperation = null,
    IBusinessEventSink? businessEvents = null,
    ICurrentUser? currentUser = null,
    PosReceiptOptions? receiptOptions = null,
    IReceiptPrinter? receiptPrinter = null,
    ICashDrawer? cashDrawer = null)
{
    private bool TracksCash => cashRecorder is not null && cashShifts is not null;

    public async Task<Result<RefundOutcome>> HandleAsync(RefundSaleCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(POS.Application.Security.POSCapabilities.CreateSale, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<RefundOutcome>(allowed.Error);

        if (string.IsNullOrWhiteSpace(command.Reason))
            return Result.Failure<RefundOutcome>(Error.Validation("POS.Refund.ReasonRequired", "Give a reason for the return."));
        if (!Enum.IsDefined(command.Method))
            return Result.Failure<RefundOutcome>(Error.Validation("POS.Refund.MethodInvalid", "Choose how the money goes back."));
        if (command.Method == POSPaymentMethod.Other && string.IsNullOrWhiteSpace(command.MethodDetail))
            return Result.Failure<RefundOutcome>(Error.Validation("POS.Refund.MethodDetailRequired", "Describe how the money goes back (for example: bank transfer)."));

        // 1. The sale, and the time limit
        var sale = await salesReader.GetReturnableAsync(command.SaleId, cancellationToken);
        if (sale is null)
            return Result.Failure<RefundOutcome>(Error.NotFound("POS.Refund.SaleNotFound", "No completed sale was found to return."));

        var days = Math.Max(0, (returnOptions ?? new PosReturnOptions()).DaysAllowed);
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        if (now - sale.CompletedAtUtc > TimeSpan.FromDays(days))
        {
            var late = await authorization.AuthorizeAsync(POS.Application.Security.POSCapabilities.LateReturns, cancellationToken);
            if (late.IsFailure)
                return Result.Failure<RefundOutcome>(Error.Unauthorized("POS.Refund.TooLate",
                    $"This sale is more than {days} day(s) old. A return after the limit needs someone allowed to accept late returns."));
        }

        // The warehouse the sale was sold from (the till session of its cart) - needed only for the lines going back to stock
        Guid? warehouseId = null;
        if (command.Lines.Any(l => l.BackToStock && l.Quantity > 0m))
        {
            var cart = await cartRepository.GetBySaleIdAsync(command.SaleId, cancellationToken);
            var session = cart is null ? null : await sessionRepository.GetByIdAsync(cart.SessionId, cancellationToken);
            if (session is null)
                return Result.Failure<RefundOutcome>(Error.Conflict("POS.Refund.WarehouseUnknown",
                    "The warehouse this sale came from is not known, so the items cannot go back into stock. Mark them as damaged instead."));
            warehouseId = session.WarehouseId;
        }

        Func<Task<Result<RefundOutcome>>> commit = () => CommitAsync(command, warehouseId, cancellationToken);
        var committed = atomicOperation is null ? await commit() : await atomicOperation.ExecuteAsync(commit, cancellationToken);
        if (committed.IsFailure) return committed;

        await businessEvents.TryRecordAsync(Refunded(command, committed.Value));
        return Result.Success(committed.Value with { HardwareNotices = await RunPeripheralsAsync(command, committed.Value) });
    }

    /// <summary>MISS-10c: the return receipt (always, when a printer is set up) and the drawer for a cash refund. Never fails the refund.</summary>
    private async Task<IReadOnlyList<POSHardwareNotice>> RunPeripheralsAsync(RefundSaleCommand command, RefundOutcome done)
    {
        var options = receiptOptions ?? new PosReceiptOptions();
        var notices = new List<POSHardwareNotice>();
        if (receiptPrinter is not null)
        {
            var receipt = ReturnReceipt(command, done, options);
            await NoticeIfFailedAsync(notices, "receipt printer", "the return receipt could not be printed", () => receiptPrinter.PrintAsync(receipt, CancellationToken.None));
        }

        if (options.AutoOpenDrawerOnCashSale && cashDrawer is not null && done.Method == POSPaymentMethod.Cash && done.RefundTotal > 0m)
            await NoticeIfFailedAsync(notices, "cash drawer", "the cash drawer could not be opened", () => cashDrawer.OpenAsync(CancellationToken.None));

        return notices;
    }

    /// <summary>The printer-independent return receipt: what came back (damaged lines marked), the refund, how it was given, the reason.</summary>
    public ReceiptDocument ReturnReceipt(RefundSaleCommand command, RefundOutcome done, PosReceiptOptions options)
    {
        var clock = timeProvider ?? TimeProvider.System;
        var method = command.Method == POSPaymentMethod.Other && !string.IsNullOrWhiteSpace(command.MethodDetail)
            ? $"{command.Method} ({command.MethodDetail.Trim()})"
            : command.Method.ToString();
        return new ReceiptDocument(
            options.StoreName,
            options.HeaderLines ?? [],
            "R-" + done.ReturnId.ToString("N")[..8].ToUpperInvariant(),
            TimeZoneInfo.ConvertTime(clock.GetUtcNow(), clock.LocalTimeZone),
            currentUser is { IsAuthenticated: true } me ? me.UserName : null,
            done.Lines.Select(l => new ReceiptLine(l.ProductName, l.Quantity, l.UnitPrice, l.Refund, WrittenOff: !l.BackToStock)).ToList(),
            done.RefundTotal,
            new ReceiptPayment(method, done.RefundTotal),
            options.FooterLines ?? [],
            Return: new ReceiptReturn("S-" + done.SaleId.ToString("N")[..8].ToUpperInvariant(), command.Reason.Trim()));
    }

    private static async Task NoticeIfFailedAsync(List<POSHardwareNotice> notices, string device, string what, Func<Task<Result>> call)
    {
        Result result;
        try
        {
            result = await HardwareGuard.RunAsync(device, call);
        }
        catch (Exception ex)
        {
            result = HardwareErrors.Failed(device, ex.Message);
        }

        // A missing device is the normal case, not a problem to report.
        if (result.IsFailure && !HardwareErrors.IsNotConfigured(result.Error))
            notices.Add(new POSHardwareNotice(device, result.Error.Code, $"The refund was given and saved, but {what}: {result.Error.Description}"));
    }

    private async Task<Result<RefundOutcome>> CommitAsync(RefundSaleCommand command, Guid? warehouseId, CancellationToken cancellationToken)
    {
        // 2. The return, recorded by Sales (its rules decide the quantities and the money)
        var recorded = await salesService.RecordReturnAsync(command.SaleId,
            command.Lines.Select(l => new ReturnLineRequest(l.SaleItemId, l.Quantity, l.BackToStock)).ToList(), command.Reason.Trim(), cancellationToken);
        if (!recorded.IsSuccess)
            return Result.Failure<RefundOutcome>(Error.Validation(recorded.ErrorCode ?? "POS.Refund.ReturnRefused", recorded.ErrorMessage ?? "The return was refused."));

        var reference = $"Return {recorded.ReturnId} of sale {command.SaleId}";
        var by = currentUser is { IsAuthenticated: true } me ? me.UserName : null;

        // 3. Back into stock (damaged lines are written off: nothing comes back)
        foreach (var line in recorded.Lines.Where(l => l.BackToStock))
        {
            var received = await stockReceipts.ReceiveStockAsync(line.CatalogProductId, warehouseId!.Value, line.Quantity, reference, cancellationToken);
            if (!received.IsSuccess)
                return Result.Failure<RefundOutcome>(Error.Failure("POS.Refund.StockFailed",
                    $"'{line.ProductName}' could not go back into stock: {received.ErrorMessage} Nothing was saved."));
        }

        // 4. The refund (money paid back) - only when the Payments module is installed and there is money to give back
        Guid? refundId = null;
        if (paymentService is not null && recorded.RefundTotal > 0m)
        {
            var refund = await paymentService.RecordRefundAsync(new RecordRefundRequest("return", recorded.ReturnId, recorded.RefundTotal,
                (PaymentMethodContract)(int)command.Method, command.MethodDetail, by), cancellationToken);
            if (!refund.IsSuccess)
                return Result.Failure<RefundOutcome>(Error.Failure("POS.Refund.PaymentFailed", $"The refund could not be recorded: {refund.ErrorMessage} Nothing was saved."));
            refundId = refund.PaymentId;
        }

        // 5. Cash out of this till's drawer shift
        if (command.Method == POSPaymentMethod.Cash && TracksCash && recorded.RefundTotal > 0m)
        {
            var drawer = (cashOptions ?? new PosCashOptions()).DrawerCode;
            var shift = await cashShifts!.GetOpenSessionAsync(drawer, cancellationToken);
            if (shift is null)
                return Result.Failure<RefundOutcome>(Error.Conflict("POS.Refund.CashDrawerNotOpen",
                    $"The cash drawer '{drawer}' has no open shift. Open it on the Cash drawer screen, or give the money back by card. Nothing was saved."));
            if (shift.Balance < recorded.RefundTotal)
                return Result.Failure<RefundOutcome>(Error.Conflict("POS.Refund.NotEnoughCash",
                    $"The drawer holds {Money(shift.Balance)}, less than the refund of {Money(recorded.RefundTotal)}. Nothing was saved."));

            var cash = await cashRecorder!.RecordMovementAsync(new RecordCashMovementRequest(shift.SessionId, CashMovementKindContract.CashRefund,
                recorded.RefundTotal, Reason: command.Reason.Trim(), ReferenceType: "return", ReferenceId: recorded.ReturnId, RecordedBy: by), cancellationToken);
            if (!cash.IsSuccess)
                return Result.Failure<RefundOutcome>(Error.Failure("POS.Refund.CashFailed", $"The cash could not be taken from the drawer: {cash.ErrorMessage} Nothing was saved."));
        }

        return Result.Success(new RefundOutcome(recorded.ReturnId, command.SaleId, recorded.RefundTotal, command.Method, recorded.Lines, refundId));
    }

    private static BusinessEvent Refunded(RefundSaleCommand command, RefundOutcome done)
    {
        var units = done.Lines.Sum(l => l.Quantity).ToString("0.###", CultureInfo.InvariantCulture);
        var damaged = done.Lines.Where(l => !l.BackToStock).Sum(l => l.Quantity);
        var note = damaged > 0m ? $", {damaged.ToString("0.###", CultureInfo.InvariantCulture)} written off as damaged" : string.Empty;
        return BusinessEvent.Create("pos", "sale.returned", "sale", done.SaleId.ToString(),
            $"Return of {units} unit(s) on {done.Lines.Count} line(s){note}; {Money(done.RefundTotal)} given back by {done.Method.ToString().ToLowerInvariant()}. Reason: {command.Reason.Trim()}",
            $"return={done.ReturnId};refund={done.RefundPaymentId?.ToString() ?? "none"}");
    }

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
