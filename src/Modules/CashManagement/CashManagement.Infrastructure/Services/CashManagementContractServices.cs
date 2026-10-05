using CashManagement.Application.Commands;
using CashManagement.Application.DTOs;
using CashManagement.Application.Queries;
using CashManagement.Contracts.Interfaces;
using CashManagement.Contracts.Models;
using CashManagement.Domain.Enums;

namespace CashManagement.Infrastructure.Services;

/// <summary>Implements ICashMovementRecorder from CashManagement.Contracts by wrapping the record handler.</summary>
internal sealed class CashMovementRecorder(RecordCashMovementCommandHandler handler) : ICashMovementRecorder
{
    public async Task<RecordCashMovementResult> RecordMovementAsync(RecordCashMovementRequest request, CancellationToken cancellationToken = default)
    {
        var result = await handler.HandleAsync(new RecordCashMovementCommand(
            request.SessionId, (CashMovementKind)(int)request.Kind, request.Amount, request.Reason,
            request.ReferenceType, request.ReferenceId, request.RecordedBy), cancellationToken);

        return result.IsSuccess
            ? RecordCashMovementResult.Success(result.Value.MovementId, result.Value.BalanceAfter)
            : RecordCashMovementResult.Failure(result.Error.Code, result.Error.Description);
    }
}

/// <summary>Implements ICashSessionReader from CashManagement.Contracts (read-only).</summary>
internal sealed class CashSessionReader(GetCashSessionQueryHandler getHandler, GetOpenCashSessionQueryHandler openHandler) : ICashSessionReader
{
    public async Task<CashSessionResult?> GetOpenSessionAsync(string drawerCode, CancellationToken cancellationToken = default)
        => ToResult(await openHandler.HandleAsync(new GetOpenCashSessionQuery(drawerCode), cancellationToken));

    public async Task<CashSessionResult?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => ToResult(await getHandler.HandleAsync(new GetCashSessionQuery(sessionId), cancellationToken));

    private static CashSessionResult? ToResult(CashSessionDto? s)
        => s is null
            ? null
            : new CashSessionResult(s.SessionId, s.DrawerCode, s.OpenedBy, s.OpeningFloat, s.Balance, s.Status == CashSessionStatus.Open, s.OpenedAt);
}
