using Audit.Application.Commands;
using Audit.Application.Queries;
using Audit.Contracts.Interfaces;
using Audit.Contracts.Models;

namespace Audit.Infrastructure.Services;

/// <summary>Implements IAuditRecorder from Audit.Contracts by wrapping the record handler.</summary>
internal sealed class AuditRecorder(RecordAuditEntryCommandHandler handler) : IAuditRecorder
{
    public async Task<AuditRecordResult> RecordAsync(AuditRecordRequest request, CancellationToken cancellationToken = default)
    {
        var result = await handler.HandleAsync(new RecordAuditEntryCommand(
            request.Module, request.Action, request.EntityType, request.EntityId, request.ActorId,
            request.ActorName, request.Summary, request.Details, request.OccurredAt), cancellationToken);

        return result.IsSuccess
            ? AuditRecordResult.Success(result.Value)
            : AuditRecordResult.Failure(result.Error.Code, result.Error.Description);
    }
}

/// <summary>Implements IAuditReader from Audit.Contracts (read-only).</summary>
internal sealed class AuditReader(QueryAuditEntriesQueryHandler queryHandler, GetAuditEntryQueryHandler getHandler) : IAuditReader
{
    public async Task<AuditPage> QueryAsync(AuditEntryFilter? filter = null, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        filter ??= new AuditEntryFilter();
        var result = await queryHandler.ExecuteAsync(new QueryAuditEntriesQuery(
            filter.Module, filter.Action, filter.EntityType, filter.EntityId, filter.ActorId, filter.From, filter.To, page, pageSize), cancellationToken);

        return new AuditPage(
            result.Items.Select(e => new AuditEntryResult(
                e.EntryId, e.OccurredAt, e.Module, e.Action, e.EntityType, e.EntityId, e.ActorId, e.ActorName, e.Summary, e.Details)).ToList(),
            result.TotalCount, result.Page, result.PageSize);
    }

    public async Task<AuditEntryResult?> GetAsync(Guid entryId, CancellationToken cancellationToken = default)
    {
        var e = await getHandler.ExecuteAsync(new GetAuditEntryQuery(entryId), cancellationToken);
        return e is null
            ? null
            : new AuditEntryResult(e.EntryId, e.OccurredAt, e.Module, e.Action, e.EntityType, e.EntityId, e.ActorId, e.ActorName, e.Summary, e.Details);
    }
}
