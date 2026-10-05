using Platform.Core.Results;

namespace Audit.Domain.ValueObjects
{
    public readonly record struct AuditEntryId(Guid Value)
    {
        public static AuditEntryId New() => new(Guid.NewGuid());
        public static AuditEntryId Empty => new(Guid.Empty);
        public override string ToString() => Value.ToString();
    }
}

namespace Audit.Domain.Entities
{
    using Audit.Domain.ValueObjects;

    /// <summary>
    /// One immutable fact: "this actor did this action to this thing at this time" (aggregate root).
    ///
    /// The log is append-only: an entry has a factory and no mutators, and the module offers no update or delete.
    /// Everything about WHAT happened is a plain string chosen by the recording module (Module, Action, EntityType, EntityId) -
    /// Audit does not know the other modules and holds no foreign keys into them.
    /// </summary>
    public sealed class AuditEntry
    {
        public const int MaxModuleLength = 50;
        public const int MaxActionLength = 100;
        public const int MaxEntityTypeLength = 100;
        public const int MaxEntityIdLength = 100;
        public const int MaxActorNameLength = 100;
        public const int MaxSummaryLength = 500;
        public const int MaxDetailsLength = 4000;

        private AuditEntry() { }

        public AuditEntryId Id { get; private set; }

        /// <summary>When it happened (UTC). Defaults to the time of recording.</summary>
        public DateTime OccurredAt { get; private set; }

        /// <summary>The module that recorded the entry, lower-case (e.g. "sales").</summary>
        public string Module { get; private set; } = string.Empty;

        /// <summary>What was done, lower-case (e.g. "sale.cancelled").</summary>
        public string Action { get; private set; } = string.Empty;

        /// <summary>The kind of thing it happened to, lower-case (e.g. "sale").</summary>
        public string? EntityType { get; private set; }
        public string? EntityId { get; private set; }

        /// <summary>Who did it, if known: a Users user ID held as a plain value (no foreign key).</summary>
        public Guid? ActorId { get; private set; }

        public string? ActorName { get; private set; }
        public string? Summary { get; private set; }
        public string? Details { get; private set; }

        public static Result<AuditEntry> Record(
            string module, string action, string? entityType = null, string? entityId = null,
            Guid? actorId = null, string? actorName = null, string? summary = null, string? details = null, DateTime? occurredAt = null)
        {
            if (string.IsNullOrWhiteSpace(module))
                return Result.Failure<AuditEntry>(Error.Validation("Audit.Entry.ModuleRequired", "The module that performed the action is required."));
            if (string.IsNullOrWhiteSpace(action))
                return Result.Failure<AuditEntry>(Error.Validation("Audit.Entry.ActionRequired", "The action is required."));

            var length = CheckLength(module, MaxModuleLength, "Audit.Entry.ModuleTooLong", "The module")
                      ?? CheckLength(action, MaxActionLength, "Audit.Entry.ActionTooLong", "The action")
                      ?? CheckLength(entityType, MaxEntityTypeLength, "Audit.Entry.EntityTypeTooLong", "The entity type")
                      ?? CheckLength(entityId, MaxEntityIdLength, "Audit.Entry.EntityIdTooLong", "The entity ID")
                      ?? CheckLength(actorName, MaxActorNameLength, "Audit.Entry.ActorNameTooLong", "The actor name")
                      ?? CheckLength(summary, MaxSummaryLength, "Audit.Entry.SummaryTooLong", "The summary")
                      ?? CheckLength(details, MaxDetailsLength, "Audit.Entry.DetailsTooLong", "The details");
            if (length is { } error) return Result.Failure<AuditEntry>(error);

            if (actorId == Guid.Empty)
                return Result.Failure<AuditEntry>(Error.Validation("Audit.Entry.ActorInvalid", "The actor ID cannot be an empty GUID."));

            return Result.Success(new AuditEntry
            {
                Id = AuditEntryId.New(),
                OccurredAt = ToUtc(occurredAt) ?? DateTime.UtcNow,
                Module = module.Trim().ToLowerInvariant(),
                Action = action.Trim().ToLowerInvariant(),
                EntityType = Clean(entityType)?.ToLowerInvariant(),
                EntityId = Clean(entityId),
                ActorId = actorId,
                ActorName = Clean(actorName),
                Summary = Clean(summary),
                Details = Clean(details)
            });
        }

        private static Error? CheckLength(string? value, int max, string code, string label)
            => value is not null && value.Trim().Length > max
                ? Error.Validation(code, $"{label} cannot exceed {max} characters.")
                : null;

        private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static DateTime? ToUtc(DateTime? value)
            => value is null ? null : value.Value.Kind == DateTimeKind.Utc ? value : value.Value.ToUniversalTime();
    }
}
