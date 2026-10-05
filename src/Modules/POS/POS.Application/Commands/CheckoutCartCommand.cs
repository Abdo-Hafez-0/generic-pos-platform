using Inventory.Contracts.Interfaces;
using Payments.Contracts.Interfaces;
using Payments.Contracts.Models;
using Platform.Core.Results;
using POS.Application.Abstractions;
using POS.Application.Repositories;
using POS.Contracts.Models;
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
///   8. Mark the cart checked out (stores the SaleId)
///
/// PAYMENTS (optional): when the command carries a payment request, the payment for the cart total is
/// recorded through Payments.Contracts after the sale is confirmed and before stock is issued. If the
/// Payments module is not installed the request is rejected up front; without a request nothing is
/// recorded. No real payment processing is performed. If stock issue fails the payment is voided.
///
/// CONSISTENCY: Catalog, Inventory, Sales and POS use separate DbContexts; there is no distributed
/// transaction. Failures before stock is issued cancel the sale (nothing else changed). A failure while
/// issuing stock cancels the sale but cannot roll back lines already issued (Inventory.Contracts has no
/// reversal operation yet) — the error says so. A failure completing the sale after stock was issued
/// leaves the sale Confirmed and the cart open, and the error says so.
/// </summary>
public sealed record CheckoutCartCommand(Guid CartId, string? TransactionReference = null, POSPaymentRequest? Payment = null);

/// <summary>A successful checkout: the sale, and the payment/change when a payment was recorded.</summary>
public sealed record CheckoutOutcome(Guid SaleId, Guid? PaymentId, decimal ChangeDue);

public sealed class CheckoutCartCommandHandler(
    IPosCartRepository cartRepository,
    IPosSessionRepository sessionRepository,
    IStockAvailabilityChecker stockAvailabilityChecker,
    IStockIssueService stockIssueService,
    ISalesService salesService,
    IPosUnitOfWork unitOfWork,
    IPaymentService? paymentService = null)
{
    public async Task<Result<CheckoutOutcome>> HandleAsync(
        CheckoutCartCommand command,
        CancellationToken cancellationToken = default)
    {
        // 1. Cart and session
        var cart = await cartRepository.GetByIdAsync(new PosCartId(command.CartId), cancellationToken);
        if (cart is null)
            return Result.Failure<CheckoutOutcome>(Error.NotFound(
                "POS.Checkout.CartNotFound", $"Cart '{command.CartId}' was not found."));

        if (cart.Status != PosCartStatus.Open)
            return Result.Failure<CheckoutOutcome>(Error.Conflict(
                "POS.Checkout.CartNotOpen", "Only an open cart can be checked out."));

        if (cart.Items.Count == 0)
            return Result.Failure<CheckoutOutcome>(Error.Validation(
                "POS.Checkout.CartEmpty", "An empty cart cannot be checked out."));

        var session = await sessionRepository.GetByIdAsync(cart.SessionId, cancellationToken);
        if (session is null || session.Status != PosSessionStatus.Open)
            return Result.Failure<CheckoutOutcome>(Error.Conflict(
                "POS.Checkout.SessionNotOpen", "The cart's POS session is not open."));

        // 1b. Optional payment: needs the Payments module and, for cash, enough money tendered
        if (command.Payment is not null)
        {
            if (paymentService is null)
                return Result.Failure<CheckoutOutcome>(Error.Conflict(
                    "POS.Checkout.PaymentsUnavailable", "A payment was requested but the Payments module is not installed."));

            if (command.Payment.TenderedAmount is { } tendered && tendered < cart.Total.Amount)
                return Result.Failure<CheckoutOutcome>(Error.Validation(
                    "POS.Checkout.TenderInsufficient", $"The cash tendered ({tendered}) does not cover the total ({cart.Total.Amount})."));
        }

        // 2. Stock re-validation (stock may have changed since items were added)
        foreach (var item in cart.Items)
        {
            var available = await stockAvailabilityChecker.IsAvailableAsync(
                item.CatalogProductId, session.WarehouseId, item.Quantity.Value, cancellationToken);
            if (!available)
                return Result.Failure<CheckoutOutcome>(Error.Conflict(
                    "POS.Checkout.InsufficientStock",
                    $"Insufficient stock for '{item.ProductName}' (quantity {item.Quantity.Value})."));
        }

        // 3. Create the sale in Sales (through Sales.Contracts only)
        var created = await salesService.CreateSaleAsync(
            reference: $"POS-{command.CartId:N}",
            notes: $"POS cashier: {session.CashierReference}",
            cancellationToken: cancellationToken);
        if (!created.IsSuccess)
            return Result.Failure<CheckoutOutcome>(Error.Failure(
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
                return Result.Failure<CheckoutOutcome>(Error.Failure(
                    "POS.Checkout.AddItemFailed",
                    $"Sales rejected '{item.ProductName}': {Describe(added.ErrorCode, added.ErrorMessage)} The sale was cancelled."));
            }
        }

        // 5. Confirm
        var confirmed = await salesService.ConfirmSaleAsync(saleId, cancellationToken);
        if (!confirmed.IsSuccess)
        {
            await CancelQuietlyAsync(saleId, "POS checkout failed while confirming.", cancellationToken);
            return Result.Failure<CheckoutOutcome>(Error.Failure(
                "POS.Checkout.ConfirmFailed",
                $"{Describe(confirmed.ErrorCode, confirmed.ErrorMessage)} The sale was cancelled."));
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
                return Result.Failure<CheckoutOutcome>(Error.Failure(
                    "POS.Checkout.PaymentFailed",
                    $"The payment could not be recorded: {Describe(payment.ErrorCode, payment.ErrorMessage)} The sale was cancelled."));
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
                if (paymentId is { } recorded)   // compensate: the payment was taken for a sale that will not complete
                    await paymentService!.VoidPaymentAsync(recorded, "POS checkout failed while issuing stock.", cancellationToken);

                await CancelQuietlyAsync(saleId, "POS checkout failed while issuing stock.", cancellationToken);
                var note = issued > 0
                    ? $" {issued} line(s) were already issued from Inventory and need a manual stock correction."
                    : string.Empty;
                return Result.Failure<CheckoutOutcome>(Error.Failure(
                    "POS.Checkout.StockIssueFailed",
                    $"Could not issue stock for '{item.ProductName}': {Describe(issue.ErrorCode, issue.ErrorMessage)} The sale was cancelled.{note}"));
            }

            issued++;
        }

        // 7. Complete the sale
        var completed = await salesService.CompleteSaleAsync(saleId, command.TransactionReference, cancellationToken);
        if (!completed.IsSuccess)
            return Result.Failure<CheckoutOutcome>(Error.Failure(
                "POS.Checkout.CompleteSaleFailed",
                $"Stock was issued but Sales could not complete sale '{saleId}': {Describe(completed.ErrorCode, completed.ErrorMessage)} The sale remains Confirmed and the cart open.{(paymentId is null ? string.Empty : $" Payment '{paymentId}' stays recorded.")}"));

        // 8. Record the outcome in POS
        var checkedOut = cart.MarkCheckedOut(saleId);
        if (checkedOut.IsFailure)
            return Result.Failure<CheckoutOutcome>(checkedOut.Error);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success(new CheckoutOutcome(saleId, paymentId, changeDue));
    }

    private async Task CancelQuietlyAsync(Guid saleId, string reason, CancellationToken cancellationToken)
    {
        // Best effort: the original failure is what the cashier needs to see.
        await salesService.CancelSaleAsync(saleId, reason, cancellationToken);
    }

    private static string Describe(string? code, string? message)
        => $"[{code}] {message}";
}
