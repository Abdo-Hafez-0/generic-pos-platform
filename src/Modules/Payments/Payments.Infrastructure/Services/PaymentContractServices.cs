using Payments.Application.Commands;
using Payments.Application.Queries;
using Payments.Contracts.Interfaces;
using Payments.Contracts.Models;
using Payments.Domain.Enums;

namespace Payments.Infrastructure.Services;

/// <summary>Implements IPaymentService from Payments.Contracts by wrapping the command handlers.</summary>
internal sealed class PaymentService(RecordPaymentCommandHandler recordHandler, VoidPaymentCommandHandler voidHandler) : IPaymentService
{
    public async Task<RecordPaymentResult> RecordPaymentAsync(RecordPaymentRequest request, CancellationToken cancellationToken = default)
    {
        var result = await recordHandler.HandleAsync(new RecordPaymentCommand(
            request.ReferenceType, request.ReferenceId, request.Amount, (PaymentMethod)(int)request.Method,
            request.MethodDetail, request.TenderedAmount, request.RecordedBy), cancellationToken);

        return result.IsSuccess
            ? RecordPaymentResult.Success(result.Value.PaymentId, result.Value.ChangeDue)
            : RecordPaymentResult.Failure(result.Error.Code, result.Error.Description);
    }

    public async Task<PaymentOperationResult> VoidPaymentAsync(Guid paymentId, string reason, CancellationToken cancellationToken = default)
    {
        var result = await voidHandler.HandleAsync(new VoidPaymentCommand(paymentId, reason), cancellationToken);
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
