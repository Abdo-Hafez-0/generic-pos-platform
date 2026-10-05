namespace Payments.Contracts.Models
{
    public enum PaymentMethodContract
    {
        Cash = 1,
        Card = 2,
        Other = 3
    }

    public enum PaymentStatusContract
    {
        Recorded = 1,
        Voided = 2
    }

    /// <summary>
    /// A request to record a payment for something identified generically (ReferenceType + ReferenceId, e.g. "sale" + sale ID).
    /// No payment gateway is involved: this RECORDS that a payment was taken.
    /// </summary>
    public sealed record RecordPaymentRequest(
        string ReferenceType,
        Guid ReferenceId,
        decimal Amount,
        PaymentMethodContract Method,
        string? MethodDetail = null,
        decimal? TenderedAmount = null,
        string? RecordedBy = null);

    public sealed record RecordPaymentResult(bool IsSuccess, Guid PaymentId, decimal ChangeDue, string? ErrorCode, string? ErrorMessage)
    {
        public static RecordPaymentResult Success(Guid paymentId, decimal changeDue) => new(true, paymentId, changeDue, null, null);

        public static RecordPaymentResult Failure(string errorCode, string errorMessage) => new(false, Guid.Empty, 0m, errorCode, errorMessage);
    }

    public sealed record PaymentOperationResult(bool IsSuccess, string? ErrorCode, string? ErrorMessage)
    {
        public static PaymentOperationResult Success() => new(true, null, null);

        public static PaymentOperationResult Failure(string errorCode, string errorMessage) => new(false, errorCode, errorMessage);
    }

    /// <summary>Read model of a payment. Never exposes Payments.Domain types.</summary>
    public sealed record PaymentResult(
        Guid PaymentId,
        string ReferenceType,
        Guid ReferenceId,
        decimal Amount,
        PaymentMethodContract Method,
        string? MethodDetail,
        decimal? TenderedAmount,
        PaymentStatusContract Status,
        DateTime RecordedAt);
}

namespace Payments.Contracts.Interfaces
{
    using Payments.Contracts.Models;

    /// <summary>
    /// Lets other modules (POS) record and void payments without knowing how payments are stored.
    /// Implemented by Payments.Infrastructure.Services.PaymentService. Fully offline; no gateway.
    /// </summary>
    public interface IPaymentService
    {
        Task<RecordPaymentResult> RecordPaymentAsync(RecordPaymentRequest request, CancellationToken cancellationToken = default);

        Task<PaymentOperationResult> VoidPaymentAsync(Guid paymentId, string reason, CancellationToken cancellationToken = default);
    }

    /// <summary>Read-only access to payments. Implemented by Payments.Infrastructure.Services.PaymentReader.</summary>
    public interface IPaymentReader
    {
        Task<IReadOnlyList<PaymentResult>> GetPaymentsForReferenceAsync(string referenceType, Guid referenceId, CancellationToken cancellationToken = default);

        /// <summary>Sum of the Recorded (not voided) payments for the reference.</summary>
        Task<decimal> GetTotalPaidAsync(string referenceType, Guid referenceId, CancellationToken cancellationToken = default);
    }
}
