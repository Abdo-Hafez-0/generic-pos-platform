using Platform.Core.Results;

namespace CashManagement.Domain.ValueObjects
{
    public readonly record struct CashSessionId(Guid Value)
    {
        public static CashSessionId New() => new(Guid.NewGuid());
        public static CashSessionId Empty => new(Guid.Empty);
        public override string ToString() => Value.ToString();
    }

    public readonly record struct CashMovementId(Guid Value)
    {
        public static CashMovementId New() => new(Guid.NewGuid());
        public static CashMovementId Empty => new(Guid.Empty);
        public override string ToString() => Value.ToString();
    }

    /// <summary>A non-negative amount of cash rounded to 4 decimals. Currency handling is a later concern.</summary>
    public readonly record struct CashAmount(decimal Value)
    {
        public static CashAmount Zero => new(0m);

        public static Result<CashAmount> Create(decimal value, string code, string label)
            => value < 0m
                ? Result.Failure<CashAmount>(Error.Validation(code, $"{label} cannot be negative."))
                : Result.Success(new CashAmount(decimal.Round(value, 4, MidpointRounding.AwayFromZero)));
    }
}

namespace CashManagement.Domain.Enums
{
    public enum CashSessionStatus
    {
        Open = 1,
        Closed = 2
    }

    public enum CashMovementKind
    {
        /// <summary>Cash added to the drawer by hand (e.g. more change). Reason required.</summary>
        PayIn = 1,

        /// <summary>Cash taken out of the drawer by hand (e.g. a supplier paid from the till). Reason required.</summary>
        PayOut = 2,

        /// <summary>Cash received for a sale (recorded by the selling module through the contract).</summary>
        CashSale = 3,

        /// <summary>Cash given back to a customer.</summary>
        CashRefund = 4
    }
}

namespace CashManagement.Domain.Entities
{
    using CashManagement.Domain.Enums;
    using CashManagement.Domain.ValueObjects;

    /// <summary>
    /// One cash drawer shift (aggregate root): opened with a float, changed only by recorded movements, closed with a counted amount.
    ///
    /// CashManagement tracks cash in the drawer ONLY. What a movement is for is an optional generic reference (ReferenceType +
    /// ReferenceId, e.g. "sale" + a Sales ID) held as plain values - never a navigation into another module - and "who" is a plain
    /// string. Nothing here moves real money or talks to hardware.
    /// </summary>
    public sealed class CashSession
    {
        public const int MaxDrawerCodeLength = 30;
        public const int MaxPersonLength = 100;
        public const int MaxNotesLength = 500;

        private readonly List<CashMovement> _movements = [];

        private CashSession() { }

        public CashSessionId Id { get; private set; }

        /// <summary>Which till this shift belongs to, upper-case (e.g. "MAIN"). Only one session per drawer can be open at a time.</summary>
        public string DrawerCode { get; private set; } = string.Empty;

        public string OpenedBy { get; private set; } = string.Empty;
        public CashAmount OpeningFloat { get; private set; }
        public CashSessionStatus Status { get; private set; }
        public DateTime OpenedAt { get; private set; }
        public string? Notes { get; private set; }

        public DateTime? ClosedAt { get; private set; }
        public string? ClosedBy { get; private set; }

        /// <summary>What the cashier counted at close.</summary>
        public CashAmount? CountedAmount { get; private set; }

        /// <summary>What the drawer should have held at close (float plus movements).</summary>
        public CashAmount? ExpectedAmount { get; private set; }

        /// <summary>Counted minus expected: negative is a shortage, positive an overage.</summary>
        public decimal? Variance { get; private set; }

        public IReadOnlyCollection<CashMovement> Movements => _movements;

        /// <summary>The cash the drawer should hold right now.</summary>
        public decimal Balance => OpeningFloat.Value + _movements.Sum(m => m.SignedAmount);

        public static Result<CashSession> Open(string drawerCode, string openedBy, decimal openingFloat, string? notes = null)
        {
            if (string.IsNullOrWhiteSpace(drawerCode))
                return Result.Failure<CashSession>(Error.Validation("CashManagement.Session.DrawerRequired", "A drawer code is required."));
            if (drawerCode.Trim().Length > MaxDrawerCodeLength)
                return Result.Failure<CashSession>(Error.Validation("CashManagement.Session.DrawerTooLong", $"The drawer code cannot exceed {MaxDrawerCodeLength} characters."));

            var person = ValidatePerson(openedBy, "CashManagement.Session.OpenedByRequired", "CashManagement.Session.OpenedByTooLong", "The opening cashier");
            if (person.IsFailure) return Result.Failure<CashSession>(person.Error);

            var amount = CashAmount.Create(openingFloat, "CashManagement.Session.FloatNegative", "The opening float");
            if (amount.IsFailure) return Result.Failure<CashSession>(amount.Error);

            var note = ValidateNotes(notes);
            if (note.IsFailure) return Result.Failure<CashSession>(note.Error);

            return Result.Success(new CashSession
            {
                Id = CashSessionId.New(),
                DrawerCode = drawerCode.Trim().ToUpperInvariant(),
                OpenedBy = person.Value,
                OpeningFloat = amount.Value,
                Status = CashSessionStatus.Open,
                OpenedAt = DateTime.UtcNow,
                Notes = Clean(notes)
            });
        }

        /// <summary>
        /// Records cash going in or out. Outflows cannot take the drawer below zero. A movement with a reference may only be recorded once
        /// per kind, so a retry after a failure never counts the same sale twice.
        /// </summary>
        public Result<CashMovement> RecordMovement(
            CashMovementKind kind, decimal amount, string? reason = null, string? referenceType = null, Guid? referenceId = null, string? recordedBy = null)
        {
            if (Status != CashSessionStatus.Open)
                return Result.Failure<CashMovement>(Error.Conflict("CashManagement.Session.NotOpen", "Movements can only be recorded in an open session."));
            if (!Enum.IsDefined(kind))
                return Result.Failure<CashMovement>(Error.Validation("CashManagement.Movement.KindInvalid", "The movement kind is not valid."));
            if (amount <= 0m)
                return Result.Failure<CashMovement>(Error.Validation("CashManagement.Movement.InvalidAmount", "The movement amount must be greater than zero."));

            var rounded = decimal.Round(amount, 4, MidpointRounding.AwayFromZero);

            if (kind is CashMovementKind.PayIn or CashMovementKind.PayOut && string.IsNullOrWhiteSpace(reason))
                return Result.Failure<CashMovement>(Error.Validation("CashManagement.Movement.ReasonRequired", "A reason is required for a pay-in or pay-out."));
            if (reason is not null && reason.Trim().Length > 200)
                return Result.Failure<CashMovement>(Error.Validation("CashManagement.Movement.ReasonTooLong", "The reason cannot exceed 200 characters."));

            var hasType = !string.IsNullOrWhiteSpace(referenceType);
            if (hasType != (referenceId is not null && referenceId != Guid.Empty))
                return Result.Failure<CashMovement>(Error.Validation("CashManagement.Movement.ReferenceIncomplete", "A reference needs both a type and an ID."));
            if (hasType && referenceType!.Trim().Length > 50)
                return Result.Failure<CashMovement>(Error.Validation("CashManagement.Movement.ReferenceTypeTooLong", "The reference type cannot exceed 50 characters."));

            if (recordedBy is not null && recordedBy.Trim().Length > MaxPersonLength)
                return Result.Failure<CashMovement>(Error.Validation("CashManagement.Movement.RecordedByTooLong", $"The recorded-by value cannot exceed {MaxPersonLength} characters."));

            var type = hasType ? referenceType!.Trim().ToLowerInvariant() : null;
            if (type is not null && _movements.Any(m => m.Kind == kind && m.ReferenceType == type && m.ReferenceId == referenceId))
                return Result.Failure<CashMovement>(Error.Conflict(
                    "CashManagement.Movement.DuplicateReference", $"A {kind} movement for {type} '{referenceId}' is already recorded in this session."));

            var movement = new CashMovement(Id, kind, rounded, Clean(reason), type, hasType ? referenceId : null, Clean(recordedBy));
            if (movement.SignedAmount < 0m && Balance + movement.SignedAmount < 0m)
                return Result.Failure<CashMovement>(Error.Conflict(
                    "CashManagement.Movement.InsufficientCash", $"The drawer only holds {Balance}; {rounded} cannot be taken out."));

            _movements.Add(movement);
            return Result.Success(movement);
        }

        public Result Close(decimal countedAmount, string closedBy, string? notes = null)
        {
            if (Status != CashSessionStatus.Open)
                return Result.Failure(Error.Conflict("CashManagement.Session.NotOpen", "The session is already closed."));

            var counted = CashAmount.Create(countedAmount, "CashManagement.Session.CountedNegative", "The counted amount");
            if (counted.IsFailure) return Result.Failure(counted.Error);

            var person = ValidatePerson(closedBy, "CashManagement.Session.ClosedByRequired", "CashManagement.Session.ClosedByTooLong", "The closing cashier");
            if (person.IsFailure) return Result.Failure(person.Error);

            var note = ValidateNotes(notes);
            if (note.IsFailure) return note;

            var expected = new CashAmount(Balance);
            CountedAmount = counted.Value;
            ExpectedAmount = expected;
            Variance = counted.Value.Value - expected.Value;
            ClosedBy = person.Value;
            ClosedAt = DateTime.UtcNow;
            Status = CashSessionStatus.Closed;
            if (!string.IsNullOrWhiteSpace(notes)) Notes = string.IsNullOrEmpty(Notes) ? notes.Trim() : $"{Notes}\n{notes.Trim()}";
            return Result.Success();
        }

        private static Result<string> ValidatePerson(string? value, string requiredCode, string longCode, string label)
        {
            if (string.IsNullOrWhiteSpace(value))
                return Result.Failure<string>(Error.Validation(requiredCode, $"{label} is required."));
            if (value.Trim().Length > MaxPersonLength)
                return Result.Failure<string>(Error.Validation(longCode, $"{label} cannot exceed {MaxPersonLength} characters."));
            return Result.Success(value.Trim());
        }

        private static Result ValidateNotes(string? notes)
            => notes is not null && notes.Trim().Length > MaxNotesLength
                ? Result.Failure(Error.Validation("CashManagement.Session.NotesTooLong", $"The notes cannot exceed {MaxNotesLength} characters."))
                : Result.Success();

        private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>A cash movement in a session (child of <see cref="CashSession"/>). Immutable once recorded.</summary>
    public sealed class CashMovement
    {
        private CashMovement() { }

        internal CashMovement(
            CashSessionId sessionId, CashMovementKind kind, decimal amount, string? reason, string? referenceType, Guid? referenceId, string? recordedBy)
        {
            Id = CashMovementId.New();
            SessionId = sessionId;
            Kind = kind;
            Amount = amount;
            Reason = reason;
            ReferenceType = referenceType;
            ReferenceId = referenceId;
            RecordedBy = recordedBy;
            RecordedAt = DateTime.UtcNow;
        }

        public CashMovementId Id { get; private set; }
        public CashSessionId SessionId { get; private set; }
        public CashMovementKind Kind { get; private set; }

        /// <summary>Always positive; the kind decides the direction.</summary>
        public decimal Amount { get; private set; }

        public string? Reason { get; private set; }
        public string? ReferenceType { get; private set; }
        public Guid? ReferenceId { get; private set; }
        public string? RecordedBy { get; private set; }
        public DateTime RecordedAt { get; private set; }

        /// <summary>Positive for cash in (pay-in, cash sale), negative for cash out (pay-out, refund).</summary>
        public decimal SignedAmount => Kind is CashMovementKind.PayIn or CashMovementKind.CashSale ? Amount : -Amount;
    }
}
