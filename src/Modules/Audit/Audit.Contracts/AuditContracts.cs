namespace Audit.Contracts.Models
{
    /// <summary>A request to append an entry to the audit log. Everything is a plain value; Audit knows no other module.</summary>
    /// <param name="Module">The recording module, e.g. "sales".</param>
    /// <param name="Action">What happened, e.g. "sale.cancelled".</param>
    /// <param name="EntityType">The kind of thing it happened to, e.g. "sale".</param>
    /// <param name="EntityId">The identifier of that thing as text.</param>
    /// <param name="ActorId">The Users user ID, if known.</param>
    /// <param name="OccurredAt">When it happened; defaults to now.</param>
    public sealed record AuditRecordRequest(
        string Module,
        string Action,
        string? EntityType = null,
        string? EntityId = null,
        Guid? ActorId = null,
        string? ActorName = null,
        string? Summary = null,
        string? Details = null,
        DateTime? OccurredAt = null);

    public sealed record AuditRecordResult(bool IsSuccess, Guid EntryId, string? ErrorCode, string? ErrorMessage)
    {
        public static AuditRecordResult Success(Guid entryId) => new(true, entryId, null, null);

        public static AuditRecordResult Failure(string errorCode, string errorMessage) => new(false, Guid.Empty, errorCode, errorMessage);
    }

    /// <summary>All filters are optional and combined with AND. Module/Action/EntityType match ignoring case; From/To are inclusive UTC bounds.</summary>
    public sealed record AuditEntryFilter(
        string? Module = null,
        string? Action = null,
        string? EntityType = null,
        string? EntityId = null,
        Guid? ActorId = null,
        DateTime? From = null,
        DateTime? To = null);

    public sealed record AuditEntryResult(
        Guid EntryId,
        DateTime OccurredAt,
        string Module,
        string Action,
        string? EntityType,
        string? EntityId,
        Guid? ActorId,
        string? ActorName,
        string? Summary,
        string? Details);

    public sealed record AuditPage(IReadOnlyList<AuditEntryResult> Items, int TotalCount, int Page, int PageSize);
}

namespace Audit.Contracts.Interfaces
{
    using Audit.Contracts.Models;

    /// <summary>
    /// Lets any module append to the audit log without knowing how it is stored. Append-only: there is deliberately no update or delete.
    /// Implemented by Audit.Infrastructure.Services.AuditRecorder.
    /// </summary>
    public interface IAuditRecorder
    {
        Task<AuditRecordResult> RecordAsync(AuditRecordRequest request, CancellationToken cancellationToken = default);
    }

    /// <summary>Read-only access to the audit log, newest first. Implemented by Audit.Infrastructure.Services.AuditReader.</summary>
    public interface IAuditReader
    {
        Task<AuditPage> QueryAsync(AuditEntryFilter? filter = null, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default);

        Task<AuditEntryResult?> GetAsync(Guid entryId, CancellationToken cancellationToken = default);
    }
}
