using Platform.Application.Abstractions.Authorization;
using Payments.Domain.Entities;
using Payments.Domain.ValueObjects;
using Platform.Core.Results;

namespace Payments.Application.Abstractions
{
    /// <summary>Saves changes to the Payments module's own persistence (PaymentsDbContext).</summary>
    public interface IPaymentsUnitOfWork
    {
        Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    }
}

namespace Payments.Application.Repositories
{
    public interface IPaymentRepository
    {
        Task<Payment?> GetByIdAsync(PaymentId id, CancellationToken cancellationToken = default);
        Task AddAsync(Payment payment, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<Payment>> ListForReferenceAsync(string referenceType, Guid referenceId, CancellationToken cancellationToken = default);
    }
}

namespace Payments.Application.DTOs
{
    public sealed record PaymentDto(
        Guid PaymentId, string ReferenceType, Guid ReferenceId, decimal Amount, Domain.Enums.PaymentMethod Method, string? MethodDetail,
        decimal? TenderedAmount, decimal ChangeDue, Domain.Enums.PaymentStatus Status, DateTime RecordedAt, DateTime? VoidedAt, string? VoidReason);
}

namespace Payments.Application.Commands
{
    using Payments.Application.Abstractions;
    using Payments.Application.Repositories;
    using Payments.Domain.Enums;

    /// <summary>Records a payment for a generically-identified reference (e.g. a sale).</summary>
    public sealed record RecordPaymentCommand(
        string ReferenceType, Guid ReferenceId, decimal Amount, PaymentMethod Method,
        string? MethodDetail = null, decimal? TenderedAmount = null, string? RecordedBy = null);

    public sealed record RecordedPayment(Guid PaymentId, decimal ChangeDue);

    public sealed class RecordPaymentCommandHandler(IPaymentRepository repository, IPaymentsUnitOfWork unitOfWork, IAuthorizationService authorization)
    {
        public async Task<Result<RecordedPayment>> HandleAsync(RecordPaymentCommand command, CancellationToken cancellationToken = default)
        {
            var allowed = await authorization.AuthorizeAsync(Payments.Application.Security.PaymentsCapabilities.RecordPayment, cancellationToken);
            if (allowed.IsFailure) return Result.Failure<RecordedPayment>(allowed.Error);

            return await ExecuteAsync(command, cancellationToken);
        }

        /// <summary>The same operation WITHOUT the capability check, for trusted calls from other modules through this module's
        /// contracts (they run inside an operation the user was already authorized for). Not reachable from UI or other modules.</summary>
        internal async Task<Result<RecordedPayment>> ExecuteAsync(RecordPaymentCommand command, CancellationToken cancellationToken = default)
        {
            var created = Payment.Record(
                command.ReferenceType, command.ReferenceId, command.Amount, command.Method,
                command.MethodDetail, command.TenderedAmount, command.RecordedBy);
            if (created.IsFailure) return Result.Failure<RecordedPayment>(created.Error);

            await repository.AddAsync(created.Value, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Success(new RecordedPayment(created.Value.Id.Value, created.Value.ChangeDue));
        }
    }

    public sealed record VoidPaymentCommand(Guid PaymentId, string Reason);

    public sealed class VoidPaymentCommandHandler(IPaymentRepository repository, IPaymentsUnitOfWork unitOfWork, IAuthorizationService authorization,
        Platform.Application.Abstractions.Auditing.IBusinessEventSink? businessEvents = null)
    {
        public async Task<Result> HandleAsync(VoidPaymentCommand command, CancellationToken cancellationToken = default)
        {
            var allowed = await authorization.AuthorizeAsync(Payments.Application.Security.PaymentsCapabilities.VoidPayment, cancellationToken);
            if (allowed.IsFailure) return allowed;

            return await ExecuteAsync(command, cancellationToken);
        }

        /// <summary>The same operation WITHOUT the capability check, for trusted calls from other modules through this module's
        /// contracts (they run inside an operation the user was already authorized for). Not reachable from UI or other modules.</summary>
        internal async Task<Result> ExecuteAsync(VoidPaymentCommand command, CancellationToken cancellationToken = default)
        {
            var payment = await repository.GetByIdAsync(new PaymentId(command.PaymentId), cancellationToken);
            if (payment is null)
                return Result.Failure(Error.NotFound("Payments.VoidPayment.PaymentNotFound", $"Payment '{command.PaymentId}' was not found."));

            var result = payment.Void(command.Reason);
            if (result.IsFailure) return result;

            await unitOfWork.SaveChangesAsync(cancellationToken);
            await Platform.Application.Abstractions.Auditing.BusinessEventSinkExtensions.TryRecordAsync(businessEvents,   // FIX-05
                Platform.Application.Abstractions.Auditing.BusinessEvent.Create("payments", "payment.voided", "payment", command.PaymentId.ToString(),
                    $"Payment of {payment.Amount.Amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} ({payment.Method}) for {payment.ReferenceType} {payment.ReferenceId} voided: {command.Reason}"));
            return Result.Success();
        }
    }
}

namespace Payments.Application.Queries
{
    using Payments.Application.DTOs;
    using Payments.Application.Repositories;

    internal static class PaymentMapping
    {
        public static PaymentDto ToDto(this Payment p) => new(
            p.Id.Value, p.ReferenceType, p.ReferenceId, p.Amount.Amount, p.Method, p.MethodDetail, p.TenderedAmount, p.ChangeDue,
            p.Status, p.RecordedAt, p.VoidedAt, p.VoidReason);
    }

    public sealed record GetPaymentQuery(Guid PaymentId);

    public sealed class GetPaymentQueryHandler(IPaymentRepository repository)
    {
        public async Task<PaymentDto?> HandleAsync(GetPaymentQuery query, CancellationToken cancellationToken = default)
            => (await repository.GetByIdAsync(new PaymentId(query.PaymentId), cancellationToken))?.ToDto();
    }

    public sealed record GetPaymentsForReferenceQuery(string ReferenceType, Guid ReferenceId);

    public sealed class GetPaymentsForReferenceQueryHandler(IPaymentRepository repository)
    {
        public async Task<IReadOnlyList<PaymentDto>> HandleAsync(GetPaymentsForReferenceQuery query, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query.ReferenceType) || query.ReferenceId == Guid.Empty) return [];

            return (await repository.ListForReferenceAsync(query.ReferenceType, query.ReferenceId, cancellationToken))
                .OrderBy(p => p.RecordedAt).Select(p => p.ToDto()).ToList();
        }
    }
}
