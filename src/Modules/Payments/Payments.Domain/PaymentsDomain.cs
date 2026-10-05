using Platform.Core.Results;

namespace Payments.Domain.ValueObjects
{
    public readonly record struct PaymentId(Guid Value)
    {
        public static PaymentId New() => new(Guid.NewGuid());
        public static PaymentId Empty => new(Guid.Empty);
        public override string ToString() => Value.ToString();
    }

    /// <summary>A strictly positive monetary amount (rounded to 4 decimals). Currency handling is a later concern.</summary>
    public readonly record struct Money(decimal Amount)
    {
        public static Result<Money> CreatePositive(decimal amount)
            => amount <= 0m
                ? Result.Failure<Money>(Error.Validation("Payments.Payment.InvalidAmount", "The payment amount must be greater than zero."))
                : Result.Success(new Money(decimal.Round(amount, 4, MidpointRounding.AwayFromZero)));
    }
}

namespace Payments.Domain.Enums
{
    /// <summary>How the customer paid. Recording a method does NOT process it: there is no gateway or hardware integration.</summary>
    public enum PaymentMethod
    {
        Cash = 1,
        Card = 2,

        /// <summary>Any other/manual method (bank transfer, voucher...). MethodDetail describes it.</summary>
        Other = 3
    }

    public enum PaymentStatus
    {
        /// <summary>The payment was received/recorded.</summary>
        Recorded = 1,

        /// <summary>The payment was cancelled (terminal). The record is kept for history.</summary>
        Voided = 2
    }
}

namespace Payments.Domain.Entities
{
    using Payments.Domain.Enums;
    using Payments.Domain.ValueObjects;

    /// <summary>
    /// A recorded payment (aggregate root).
    ///
    /// Payments owns payment records only. What the payment is FOR is a generic reference (ReferenceType + ReferenceId, e.g.
    /// "sale" + a Sales sale ID) - plain values, never a navigation into another module. Payments knows nothing about Sales, POS
    /// or the amount due; callers decide what to pay and compare totals through the contracts.
    /// </summary>
    public sealed class Payment
    {
        private Payment() { }

        public PaymentId Id { get; private set; }
        public string ReferenceType { get; private set; } = string.Empty;
        public Guid ReferenceId { get; private set; }
        public Money Amount { get; private set; }
        public PaymentMethod Method { get; private set; }
        public string? MethodDetail { get; private set; }

        /// <summary>Cash handed over (cash only). Must cover the amount; the difference is the change due.</summary>
        public decimal? TenderedAmount { get; private set; }

        public string? RecordedBy { get; private set; }
        public PaymentStatus Status { get; private set; }
        public DateTime RecordedAt { get; private set; }
        public DateTime? VoidedAt { get; private set; }
        public string? VoidReason { get; private set; }

        public decimal ChangeDue => TenderedAmount is { } t ? Math.Max(0m, t - Amount.Amount) : 0m;

        public static Result<Payment> Record(
            string referenceType, Guid referenceId, decimal amount, PaymentMethod method,
            string? methodDetail = null, decimal? tenderedAmount = null, string? recordedBy = null)
        {
            if (string.IsNullOrWhiteSpace(referenceType))
                return Result.Failure<Payment>(Error.Validation("Payments.Payment.ReferenceTypeRequired", "A reference type is required (e.g. \"sale\")."));
            if (referenceType.Trim().Length > 50)
                return Result.Failure<Payment>(Error.Validation("Payments.Payment.ReferenceTypeTooLong", "The reference type cannot exceed 50 characters."));
            if (referenceId == Guid.Empty)
                return Result.Failure<Payment>(Error.Validation("Payments.Payment.ReferenceRequired", "A reference ID is required."));
            if (!Enum.IsDefined(method))
                return Result.Failure<Payment>(Error.Validation("Payments.Payment.MethodInvalid", "The payment method is not valid."));

            var money = Money.CreatePositive(amount);
            if (money.IsFailure) return Result.Failure<Payment>(money.Error);

            if (methodDetail is not null && methodDetail.Trim().Length > 100)
                return Result.Failure<Payment>(Error.Validation("Payments.Payment.MethodDetailTooLong", "The method detail cannot exceed 100 characters."));
            if (method == PaymentMethod.Other && string.IsNullOrWhiteSpace(methodDetail))
                return Result.Failure<Payment>(Error.Validation("Payments.Payment.MethodDetailRequired", "Describe the payment method for an \"Other\" payment."));

            if (tenderedAmount is not null)
            {
                if (method != PaymentMethod.Cash)
                    return Result.Failure<Payment>(Error.Validation("Payments.Payment.TenderedOnlyForCash", "A tendered amount only applies to cash payments."));
                if (tenderedAmount.Value < money.Value.Amount)
                    return Result.Failure<Payment>(Error.Validation("Payments.Payment.TenderedTooLow", "The cash tendered does not cover the payment amount."));
            }

            if (recordedBy is not null && recordedBy.Trim().Length > 100)
                return Result.Failure<Payment>(Error.Validation("Payments.Payment.RecordedByTooLong", "The recorded-by value cannot exceed 100 characters."));

            return Result.Success(new Payment
            {
                Id = PaymentId.New(),
                ReferenceType = referenceType.Trim().ToLowerInvariant(),
                ReferenceId = referenceId,
                Amount = money.Value,
                Method = method,
                MethodDetail = string.IsNullOrWhiteSpace(methodDetail) ? null : methodDetail.Trim(),
                TenderedAmount = tenderedAmount is null ? null : decimal.Round(tenderedAmount.Value, 4, MidpointRounding.AwayFromZero),
                RecordedBy = string.IsNullOrWhiteSpace(recordedBy) ? null : recordedBy.Trim(),
                Status = PaymentStatus.Recorded,
                RecordedAt = DateTime.UtcNow
            });
        }

        public Result Void(string reason)
        {
            if (Status == PaymentStatus.Voided)
                return Result.Failure(Error.Conflict("Payments.Payment.AlreadyVoided", "The payment is already voided."));
            if (string.IsNullOrWhiteSpace(reason))
                return Result.Failure(Error.Validation("Payments.Payment.VoidReasonRequired", "A reason is required to void a payment."));
            if (reason.Trim().Length > 500)
                return Result.Failure(Error.Validation("Payments.Payment.VoidReasonTooLong", "The reason cannot exceed 500 characters."));

            Status = PaymentStatus.Voided;
            VoidReason = reason.Trim();
            VoidedAt = DateTime.UtcNow;
            return Result.Success();
        }
    }
}
