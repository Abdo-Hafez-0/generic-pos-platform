using Audit.Domain.Entities;
using Audit.Domain.ValueObjects;
using Platform.Core.Results;

namespace Audit.Application.Abstractions
{
    /// <summary>Saves changes to the Audit module's own persistence (AuditDbContext).</summary>
    public interface IAuditUnitOfWork
    {
        Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    }
}

namespace Audit.Application.Repositories
{
    /// <summary>Filter values arrive already normalised (lower-case module/action/entity type).</summary>
    public sealed record AuditFilter(
        string? Module, string? Action, string? EntityType, string? EntityId, Guid? ActorId, DateTime? From, DateTime? To);

    /// <summary>Append and read only: no update or delete exists.</summary>
    public interface IAuditEntryRepository
    {
        Task AddAsync(AuditEntry entry, CancellationToken cancellationToken = default);
        Task<AuditEntry?> GetByIdAsync(AuditEntryId id, CancellationToken cancellationToken = default);
        Task<(IReadOnlyList<AuditEntry> Items, int TotalCount)> QueryAsync(AuditFilter filter, int skip, int take, CancellationToken cancellationToken = default);
    }
}

namespace Audit.Application.DTOs
{
    public sealed record AuditEntryDto(
        Guid EntryId, DateTime OccurredAt, string Module, string Action, string? EntityType, string? EntityId,
        Guid? ActorId, string? ActorName, string? Summary, string? Details);

    public sealed record PagedAuditEntries(IReadOnlyList<AuditEntryDto> Items, int TotalCount, int Page, int PageSize);
}

namespace Audit.Application.Commands
{
    using Audit.Application.Abstractions;
    using Audit.Application.Repositories;

    /// <summary>Appends one entry to the audit log.</summary>
    public sealed record RecordAuditEntryCommand(
        string Module, string Action, string? EntityType = null, string? EntityId = null, Guid? ActorId = null,
        string? ActorName = null, string? Summary = null, string? Details = null, DateTime? OccurredAt = null);

    public sealed class RecordAuditEntryCommandHandler(IAuditEntryRepository repository, IAuditUnitOfWork unitOfWork)
    {
        public async Task<Result<Guid>> HandleAsync(RecordAuditEntryCommand command, CancellationToken cancellationToken = default)
        {
            var created = AuditEntry.Record(
                command.Module, command.Action, command.EntityType, command.EntityId, command.ActorId,
                command.ActorName, command.Summary, command.Details, command.OccurredAt);
            if (created.IsFailure) return Result.Failure<Guid>(created.Error);

            await repository.AddAsync(created.Value, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Success(created.Value.Id.Value);
        }
    }
}

namespace Audit.Application.Queries
{
    using Audit.Application.DTOs;
    using Audit.Application.Repositories;

    internal static class AuditMapping
    {
        public static AuditEntryDto ToDto(this AuditEntry e) => new(
            e.Id.Value, e.OccurredAt, e.Module, e.Action, e.EntityType, e.EntityId, e.ActorId, e.ActorName, e.Summary, e.Details);
    }

    public sealed record GetAuditEntryQuery(Guid EntryId);

    public sealed class GetAuditEntryQueryHandler(IAuditEntryRepository repository)
    {
        public async Task<AuditEntryDto?> HandleAsync(GetAuditEntryQuery query, CancellationToken cancellationToken = default)
            => (await repository.GetByIdAsync(new AuditEntryId(query.EntryId), cancellationToken))?.ToDto();
    }

    public sealed record QueryAuditEntriesQuery(
        string? Module = null, string? Action = null, string? EntityType = null, string? EntityId = null,
        Guid? ActorId = null, DateTime? From = null, DateTime? To = null, int Page = 1, int PageSize = 50);

    public sealed class QueryAuditEntriesQueryHandler(IAuditEntryRepository repository)
    {
        public const int MaxPageSize = 200;

        public async Task<PagedAuditEntries> HandleAsync(QueryAuditEntriesQuery query, CancellationToken cancellationToken = default)
        {
            var page = Math.Max(1, query.Page);
            var size = Math.Clamp(query.PageSize, 1, MaxPageSize);

            var filter = new AuditFilter(
                Normalise(query.Module), Normalise(query.Action), Normalise(query.EntityType), Clean(query.EntityId), query.ActorId,
                ToUtc(query.From), ToUtc(query.To));

            var (items, total) = await repository.QueryAsync(filter, (page - 1) * size, size, cancellationToken);
            return new PagedAuditEntries(items.Select(e => e.ToDto()).ToList(), total, page, size);
        }

        private static string? Normalise(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

        private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static DateTime? ToUtc(DateTime? value)
            => value is null ? null : value.Value.Kind == DateTimeKind.Utc ? value : value.Value.ToUniversalTime();
    }
}
