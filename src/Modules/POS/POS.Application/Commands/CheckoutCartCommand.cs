using CashManagement.Contracts.Interfaces;
using CashManagement.Contracts.Models;
using Platform.Application.Abstractions.Authorization;
using Platform.Application.Abstractions.Data;
using Inventory.Contracts.Interfaces;
using Payments.Contracts.Interfaces;
using Payments.Contracts.Models;
using Platform.Application.Abstractions.Hardware;
using Platform.Core.Results;
using POS.Application.Devices;
using POS.Application.Abstractions;
using POS.Application.Repositories;
using POS.Contracts.Models;
using POS.Domain.Entities;
using POS.Domain.Enums;
using POS.Domain.ValueObjects;
using Sales.Contracts.Interfaces;

namespace POS.Application.Commands;

// ============================================================
// CheckoutCartCommand
// ============================================================

/// <summary>
/// Hands the cart to Sales and completes the transaction.
///
/// ORCHESTRATION (POS owns the flow; each module owns its own data and lifecycle):
///   1. Validate cart (open, not empty) and session (open).
///   2. Re-validate stock for every line      -> Inventory.Contracts IStockAvailabilityChecker
///   3. Create the sale                       -> Sales.Contracts ISalesService
///   4. Add every line with the cart's price snapshot (Sales persists its own snapshot)
///   5. Confirm the sale
///   6. Issue stock for every line            -> Inventory.Contracts IStockIssueService
///   7. Complete the sale (records the SalesTransaction)
///   8. Mark the cart checked out (stores the SaleId) and SAVE. The sale is now final.
///   9. Peripherals (optional hardware): print the receipt and, for a cash payment, open the drawer. A problem here is reported
///      as a HardwareNotice on the result; it can never undo, roll back or alter the saved sale.
///
/// PAYMENTS (optional): when the command carries a payment request, the payment for the cart total is
/// recorded through Payments.Contracts after the sale is confirmed and before stock is issued. If the
/// Payments module is not installed the request is rejected up front; without a request nothing is
/// recorded. No real payment processing is performed. If stock issue fails the payment is voided.
///
/// CASH DRAWER (optional CashManagement module, FIX-04; decisions by the user): a CASH payment is also recorded as a CashSale movement
/// in the open shift of this till's drawer (<see cref="PosCashOptions.DrawerCode"/>), INSIDE the same transaction, for the cart total
/// (the change goes back to the customer), with the sale as its idempotent reference. A cash sale is refused when that drawer has no
/// open shift, so the drawer's expected balance always matches the cash taken. Without the CashManagement module nothing is recorded.
///
/// CONSISTENCY: steps 1-8 run as ONE database transaction (IAtomicOperation) across the Sales, Payments, Inventory and POS
/// contexts: the sale, its lines, the payment, the stock movements and the checked-out cart are committed together or not at
/// all. Any failure (a rejected step, an exception, a crash) leaves NO partial state, and the database does the rolling back -
/// nothing is cancelled or voided afterwards. Hosts that register no IAtomicOperation (unit-test hosts) fall back to the earlier
/// step-by-step flow with compensation (cancel the sale, void the payment).
/// </summary>
public sealed record CheckoutCartCommand(Guid CartId, string? TransactionReference = null, POSPaymentRequest? Payment = null);

/// <summary>A successful checkout: the sale, and the payment/change when a payment was recorded.</summary>
public sealed record CheckoutOutcome(Guid SaleId, Guid? PaymentId, decimal ChangeDue, IReadOnlyList<POSHardwareNotice>? HardwareNotices = null);

public sealed class CheckoutCartCommandHandler(
    IPosCartRepository cartRepository,
    IPosSessionRepository sessionRepository,
    IStockAvailabilityChecker stockAvailabilityChecker,
    IStockIssueService stockIssueService,
    ISalesService salesService,
    IPosUnitOfWork unitOfWork,
    IAuthorizationService authorization,
    IPaymentService? paymentService = null,
    IReceiptPrinter? receiptPrinter = null,
    ICashDrawer? cashDrawer = null,
    PosReceiptOptions? receiptOptions = null,
    TimeProvider? timeProvider = null,
    IAtomicOperation? atomicOperation = null,
    ICashMovementRecorder? cashRecorder = null,
    ICashSessionReader? cashShifts = null,
    PosCashOptions? cashOptions = null)
{
    /// <summary>What a committed checkout leaves behind for the peripherals step.</summary>
    private sealed record Committed(PosCart Cart, PosSession Session, Guid SaleId, Guid? PaymentId, decimal ChangeDue);

    private bool Transactional => atomicOperation is not null;

    /// <summary>Cash payments go into a drawer shift only when the CashManagement module is installed.</summary>
    private bool TracksCash => cashRecorder is not null && cashShifts is not null;

    public async Task<Result<CheckoutOutcome>> HandleAsync(
        CheckoutCartCommand command,
        CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(POS.Application.Security.POSCapabilities.CreateSale, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<CheckoutOutcome>(allowed.Error);

        // 1-8. One transaction, started BEFORE the cart is read: two checkouts of the same cart (a double click, two terminals) are
        // serialised by the database, and the second one finds the cart already checked out instead of selling it twice.
        Func<Task<Result<Committed>>> commit = () => CommitSaleAsync(command, cancellationToken);
        var committed = atomicOperation is null
            ? await commit()
            : await atomicOperation.ExecuteAsync(commit, cancellationToken);
        if (committed.IsFailure) return Result.Failure<CheckoutOutcome>(committed.Error);

        // 9. Peripherals. The sale is complete and saved; from here on only hardware can go wrong, and that can never undo it.
        var done = committed.Value;
        var notices = await RunPeripheralsAsync(done.Cart, done.Session, done.SaleId, command.Payment, done.ChangeDue);
        return Result.Success(new CheckoutOutcome(done.SaleId, done.PaymentId, done.ChangeDue, notices));
    }

    private async Task<Result<Committed>> CommitSaleAsync(CheckoutCartCommand command, CancellationToken cancellationToken)
    {
        // 1. Cart and session
        var cart = await cartRepository.GetByIdAsync(new PosCartId(command.CartId), cancellationToken);
        if (cart is null)
            return Result.Failure<Committed>(Error.NotFound(
                "POS.Checkout.CartNotFound", $"Cart '{command.CartId}' was not found."));

        if (cart.Status != PosCartStatus.Open)
            return Result.Failure<Committed>(Error.Conflict(
                "POS.Checkout.CartNotOpen", "Only an open cart can be checked out."));

        if (cart.Items.Count == 0)
            return Result.Failure<Committed>(Error.Validation(
                "POS.Checkout.CartEmpty", "An empty cart cannot be checked out."));

        var session = await sessionRepository.GetByIdAsync(cart.SessionId, cancellationToken);
        if (session is null || session.Status != PosSessionStatus.Open)
            return Result.Failure<Committed>(Error.Conflict(
                "POS.Checkout.SessionNotOpen", "The cart's POS session is not open."));

        // 1b. Optional payment: needs the Payments module and, for cash, enough money tendered
        if (command.Payment is not null)
        {
            if (paymentService is null)
                return Result.Failure<Committed>(Error.Conflict(
                    "POS.Checkout.PaymentsUnavailable", "A payment was requested but the Payments module is not installed."));

            if (command.Payment.TenderedAmount is { } tendered && tendered < cart.Total.Amount)
                return Result.Failure<Committed>(Error.Validation(
                    "POS.Checkout.TenderInsufficient", $"The cash tendered ({tendered}) does not cover the total ({cart.Total.Amount})."));
        }

        // 1c. Cash goes into an open drawer shift (FIX-04): without one the cash sale is refused before anything is written.
        Guid? cashShiftId = null;
        if (command.Payment?.Method == POSPaymentMethod.Cash && TracksCash)
        {
            var drawer = (cashOptions ?? new PosCashOptions()).DrawerCode;
            var shift = await cashShifts!.GetOpenSessionAsync(drawer, cancellationToken);
            if (shift is null)
                return Result.Failure<Committed>(Error.Conflict(
                    "POS.Checkout.CashDrawerNotOpen",
                    $"The cash drawer '{drawer}' has no open shift. Open it on the Cash drawer screen before taking cash."));

            cashShiftId = shift.SessionId;
        }

        // 2. Stock re-validation (stock may have changed since items were added)
        foreach (var item in cart.Items)
        {
            var available = await stockAvailabilityChecker.IsAvailableAsync(
                item.CatalogProductId, session.WarehouseId, item.Quantity.Value, cancellationToken);
            if (!available)
                return Result.Failure<Committed>(Error.Conflict(
                    "POS.Checkout.InsufficientStock",
                    $"Insufficient stock for '{item.ProductName}' (quantity {item.Quantity.Value})."));
        }

        // 3. Create the sale in Sales (through Sales.Contracts only)
        var created = await salesService.CreateSaleAsync(
            reference: $"POS-{command.CartId:N}",
            notes: $"POS cashier: {session.CashierReference}",
            cancellationToken: cancellationToken);
        if (!created.IsSuccess)
            return Result.Failure<Committed>(Error.Failure(
                "POS.Checkout.CreateSaleFailed", Describe(created.ErrorCode, created.ErrorMessage)));

        var saleId = created.SaleId;

        // 4. Add lines — the cart's price snapshot is passed to Sales
        foreach (var item in cart.Items)
        {
            var added = await salesService.AddItemAsync(
                saleId,
                item.CatalogProductId,
                item.Quantity.Value,
                item.UnitPrice.Amount,
                warehouseId: session.WarehouseId,
                cancellationToken: cancellationToken);

            if (!added.IsSuccess)
            {
                await CancelQuietlyAsync(saleId, "POS checkout failed while adding items.", cancellationToken);
                return Result.Failure<Committed>(Error.Failure(
                    "POS.Checkout.AddItemFailed",
                    $"Sales rejected '{item.ProductName}': {Describe(added.ErrorCode, added.ErrorMessage)} {Outcome}"));
            }
        }

        // 5. Confirm
        var confirmed = await salesService.ConfirmSaleAsync(saleId, cancellationToken);
        if (!confirmed.IsSuccess)
        {
            await CancelQuietlyAsync(saleId, "POS checkout failed while confirming.", cancellationToken);
            return Result.Failure<Committed>(Error.Failure(
                "POS.Checkout.ConfirmFailed",
                $"{Describe(confirmed.ErrorCode, confirmed.ErrorMessage)} {Outcome}"));
        }

        // 5b. Record the payment (optional Payments module) for the full cart total
        Guid? paymentId = null;
        var changeDue = 0m;
        if (command.Payment is not null)
        {
            var payment = await paymentService!.RecordPaymentAsync(new RecordPaymentRequest(
                "sale", saleId, cart.Total.Amount, (PaymentMethodContract)(int)command.Payment.Method,
                command.Payment.MethodDetail, command.Payment.TenderedAmount, session.CashierReference), cancellationToken);

            if (!payment.IsSuccess)
            {
                await CancelQuietlyAsync(saleId, "POS checkout failed while recording the payment.", cancellationToken);
                return Result.Failure<Committed>(Error.Failure(
                    "POS.Checkout.PaymentFailed",
                    $"The payment could not be recorded: {Describe(payment.ErrorCode, payment.ErrorMessage)} {Outcome}"));
            }

            paymentId = payment.PaymentId;
            changeDue = payment.ChangeDue;
        }

        // 6. Issue stock through Inventory.Contracts
        var issued = 0;
        foreach (var item in cart.Items)
        {
            var issue = await stockIssueService.IssueStockAsync(
                item.CatalogProductId,
                session.WarehouseId,
                item.Quantity.Value,
                reference: $"POS sale {saleId}",
                cancellationToken: cancellationToken);

            if (!issue.IsSuccess)
            {
                if (!Transactional && paymentId is { } recorded)   // compensate: the payment was taken for a sale that will not complete
                    await paymentService!.VoidPaymentAsync(recorded, "POS checkout failed while issuing stock.", cancellationToken);

                await CancelQuietlyAsync(saleId, "POS checkout failed while issuing stock.", cancellationToken);
                var note = !Transactional && issued > 0
                    ? $" {issued} line(s) were already issued from Inventory and need a manual stock correction."
                    : string.Empty;
                return Result.Failure<Committed>(Error.Failure(
                    "POS.Checkout.StockIssueFailed",
                    $"Could not issue stock for '{item.ProductName}': {Describe(issue.ErrorCode, issue.ErrorMessage)} {Outcome}{note}"));
            }

            issued++;
        }

        // 6b. The cash taken goes into the drawer shift (FIX-04), in the same transaction as the sale.
        if (cashShiftId is { } cashShift)
        {
            var recorded = await cashRecorder!.RecordMovementAsync(new RecordCashMovementRequest(
                cashShift, CashMovementKindContract.CashSale, cart.Total.Amount,
                ReferenceType: "sale", ReferenceId: saleId, RecordedBy: session.CashierReference), cancellationToken);

            if (!recorded.IsSuccess)
            {
                if (!Transactional && paymentId is { } taken)
                    await paymentService!.VoidPaymentAsync(taken, "POS checkout failed while recording the cash in the drawer.", cancellationToken);

                await CancelQuietlyAsync(saleId, "POS checkout failed while recording the cash in the drawer.", cancellationToken);
                var stockNote = !Transactional && issued > 0
                    ? $" {issued} line(s) were already issued from Inventory and need a manual stock correction."
                    : string.Empty;
                return Result.Failure<Committed>(Error.Failure(
                    "POS.Checkout.CashDrawerFailed",
                    $"The cash could not be recorded in the drawer: {Describe(recorded.ErrorCode, recorded.ErrorMessage)} {Outcome}{stockNote}"));
            }
        }

        // 7. Complete the sale
        var completed = await salesService.CompleteSaleAsync(saleId, command.TransactionReference, cancellationToken);
        if (!completed.IsSuccess)
            return Result.Failure<Committed>(Error.Failure(
                "POS.Checkout.CompleteSaleFailed",
                Transactional
                    ? $"Sales could not complete the sale: {Describe(completed.ErrorCode, completed.ErrorMessage)} {Outcome}"
                    : $"Stock was issued but Sales could not complete sale '{saleId}': {Describe(completed.ErrorCode, completed.ErrorMessage)} The sale remains Confirmed and the cart open.{(paymentId is null ? string.Empty : $" Payment '{paymentId}' stays recorded.")}"));

        // 8. Record the outcome in POS
        var checkedOut = cart.MarkCheckedOut(saleId);
        if (checkedOut.IsFailure)
            return Result.Failure<Committed>(checkedOut.Error);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(new Committed(cart, session, saleId, paymentId, changeDue));
    }

    private async Task<IReadOnlyList<POSHardwareNotice>> RunPeripheralsAsync(
        PosCart cart, PosSession session, Guid saleId, POSPaymentRequest? payment, decimal changeDue)
    {
        var options = receiptOptions ?? new PosReceiptOptions();
        var notices = new List<POSHardwareNotice>();

        // No cancellation token: the sale is finished, so the cashier's cancellation must not hide that. Device calls are bounded by their own timeouts.
        if (options.AutoPrintReceipt && receiptPrinter is not null)
        {
            var receiptPayment = payment is null ? null : PosReceiptFactory.ToReceiptPayment(payment, cart.Total.Amount, changeDue);
            var receipt = PosReceiptFactory.Create(cart, session, saleId, receiptPayment, options, (timeProvider ?? TimeProvider.System).GetUtcNow());
            await NoticeIfFailedAsync(notices, "receipt printer", "the receipt could not be printed",
                () => receiptPrinter.PrintAsync(receipt, CancellationToken.None));
        }

        if (options.AutoOpenDrawerOnCashSale && cashDrawer is not null && payment?.Method == POSPaymentMethod.Cash)
        {
            await NoticeIfFailedAsync(notices, "cash drawer", "the cash drawer could not be opened",
                () => cashDrawer.OpenAsync(CancellationToken.None));
        }

        return notices;
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
            notices.Add(new POSHardwareNotice(device, result.Error.Code, $"The sale was completed and saved, but {what}: {result.Error.Description}"));
    }

    /// <summary>What the cashier is told about the state of the data after a failed checkout.</summary>
    private string Outcome => Transactional
        ? "Nothing was saved: no sale, payment or stock change was made."
        : "The sale was cancelled.";

    private async Task CancelQuietlyAsync(Guid saleId, string reason, CancellationToken cancellationToken)
    {
        // The transaction rolls the sale back; only the step-by-step fallback has to cancel it by hand.
        if (Transactional) return;

        // Best effort: the original failure is what the cashier needs to see.
        await salesService.CancelSaleAsync(saleId, reason, cancellationToken);
    }

    private static string Describe(string? code, string? message)
        => $"[{code}] {message}";
}
