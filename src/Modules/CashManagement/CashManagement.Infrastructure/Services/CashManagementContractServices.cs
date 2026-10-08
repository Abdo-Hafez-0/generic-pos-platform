using CashManagement.Application.Commands;
using CashManagement.Application.DTOs;
using CashManagement.Application.Queries;
using CashManagement.Contracts.Interfaces;
using CashManagement.Contracts.Models;
using CashManagement.Domain.Enums;

namespace CashManagement.Infrastructure.Services;

/// <summary>
/// Implements ICashMovementRecorder from CashManagement.Contracts by wrapping the record handler.
///
/// FIX-06: an UNEXPECTED failure (database locked or unavailable, disk error) reaches the calling module as a failed result with a plain
/// sentence, never as an exception or database text; the details go to the log.
/// </summary>
internal sealed class CashMovementRecorder(RecordCashMovementCommandHandler handler, Microsoft.Extensions.Logging.ILogger<CashMovementRecorder>? logger = null) : ICashMovementRecorder
{
    public async Task<RecordCashMovementResult> RecordMovementAsync(RecordCashMovementRequest request, CancellationToken cancellationToken = default)
    {
        Platform.Core.Results.Result<RecordedCashMovement> result;
        try
        {
            result = await handler.ExecuteAsync(new RecordCashMovementCommand(
                request.SessionId, (CashMovementKind)(int)request.Kind, request.Amount, request.Reason,
                request.ReferenceType, request.ReferenceId, request.RecordedBy), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogError(logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CashMovementRecorder>.Instance, ex,
                "Recording a cash movement in session {SessionId} failed unexpectedly.", request.SessionId);
            return RecordCashMovementResult.Failure("CashManagement.OperationFailed",
                "The cash could not be recorded in the drawer and the drawer was not changed. Try again; if it keeps failing, contact support.");
        }

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
