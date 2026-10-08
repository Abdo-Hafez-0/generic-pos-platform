using Payments.Application.Commands;
using Payments.Application.Queries;
using Payments.Contracts.Interfaces;
using Payments.Contracts.Models;
using Payments.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Payments.Infrastructure.Services;

/// <summary>
/// Implements IPaymentService from Payments.Contracts by wrapping the command handlers.
///
/// FIX-06: an UNEXPECTED failure (database locked or unavailable, disk error) reaches the calling module as a failed result with a plain
/// sentence, never as an exception or database text; the details go to the log.
/// </summary>
internal sealed class PaymentService(RecordPaymentCommandHandler recordHandler, VoidPaymentCommandHandler voidHandler, ILogger<PaymentService>? logger = null) : IPaymentService
{
    private const string OperationFailedCode = "Payments.OperationFailed";

    public async Task<RecordPaymentResult> RecordPaymentAsync(RecordPaymentRequest request, CancellationToken cancellationToken = default)
    {
        Platform.Core.Results.Result<Payments.Application.Commands.RecordedPayment> result;
        try
        {
            result = await recordHandler.ExecuteAsync(new RecordPaymentCommand(
                request.ReferenceType, request.ReferenceId, request.Amount, (PaymentMethod)(int)request.Method,
                request.MethodDetail, request.TenderedAmount, request.RecordedBy), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogError(ex, "Recording a payment for {ReferenceType} {ReferenceId} failed unexpectedly.", request.ReferenceType, request.ReferenceId);
            return RecordPaymentResult.Failure(OperationFailedCode, "The payment could not be recorded and Payments changed nothing. Try again; if it keeps failing, contact support.");
        }

        return result.IsSuccess
            ? RecordPaymentResult.Success(result.Value.PaymentId, result.Value.ChangeDue)
            : RecordPaymentResult.Failure(result.Error.Code, result.Error.Description);
    }

    public async Task<PaymentOperationResult> VoidPaymentAsync(Guid paymentId, string reason, CancellationToken cancellationToken = default)
    {
        Platform.Core.Results.Result result;
        try
        {
            result = await voidHandler.ExecuteAsync(new VoidPaymentCommand(paymentId, reason), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogError(ex, "Voiding payment {PaymentId} failed unexpectedly.", paymentId);
            return PaymentOperationResult.Failure(OperationFailedCode, "The payment could not be voided and Payments changed nothing. Try again; if it keeps failing, contact support.");
        }

        return result.IsSuccess
            ? PaymentOperationResult.Success()
            : PaymentOperationResult.Failure(result.Error.Code, result.Error.Description);
    }
}

/// <summary>Implements IPaymentReader from Payments.Contracts (read-only).</summary>
internal sealed class PaymentReader(GetPaymentsForReferenceQueryHandler handler) : IPaymentReader
{
    public async Task<IReadOnlyList<PaymentResult>> GetPaymentsForReferenceAsync(string referenceType, Guid referenceId, CancellationToken cancellationToken = default)
        => (await handler.HandleAsync(new GetPaymentsForReferenceQuery(referenceType, referenceId), cancellationToken))
            .Select(p => new PaymentResult(
                p.PaymentId, p.ReferenceType, p.ReferenceId, p.Amount, (PaymentMethodContract)(int)p.Method, p.MethodDetail,
                p.TenderedAmount, (PaymentStatusContract)(int)p.Status, p.RecordedAt))
            .ToList();

    public async Task<decimal> GetTotalPaidAsync(string referenceType, Guid referenceId, CancellationToken cancellationToken = default)
        => (await GetPaymentsForReferenceAsync(referenceType, referenceId, cancellationToken))
            .Where(p => p.Status == PaymentStatusContract.Recorded)
            .Sum(p => p.Amount);
}
