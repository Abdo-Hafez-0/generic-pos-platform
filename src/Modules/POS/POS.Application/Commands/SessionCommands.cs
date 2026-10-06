using Platform.Application.Abstractions.Authorization;
using Platform.Core.Results;
using POS.Application.Abstractions;
using POS.Application.Repositories;
using POS.Domain.Entities;
using POS.Domain.ValueObjects;

namespace POS.Application.Commands;

// ============================================================
// OpenPosSessionCommand
// ============================================================

/// <summary>
/// Opens a till session. When a user is signed in, the session is attributed to THAT user: the supplied
/// <see cref="CashierReference"/> is only used when nobody is signed in (hosts without authentication), so a screen
/// cannot open a session in someone else's name and receipts always show who really sold.
/// </summary>
public sealed record OpenPosSessionCommand(string CashierReference, Guid WarehouseId);

public sealed class OpenPosSessionCommandHandler(
    IPosSessionRepository sessionRepository,
    IPosUnitOfWork unitOfWork,
    IAuthorizationService authorization,
    ICurrentUser? currentUser = null)
{
    public async Task<Result<Guid>> HandleAsync(
        OpenPosSessionCommand command,
        CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(POS.Application.Security.POSCapabilities.ManageSession, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<Guid>(allowed.Error);

        var cashier = currentUser is { IsAuthenticated: true } ? currentUser.UserName : command.CashierReference;
        var sessionResult = PosSession.Open(cashier, command.WarehouseId);
        if (sessionResult.IsFailure)
            return Result.Failure<Guid>(sessionResult.Error);

        await sessionRepository.AddAsync(sessionResult.Value, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(sessionResult.Value.Id.Value);
    }
}

// ============================================================
// ClosePosSessionCommand
// ============================================================

/// <summary>
/// Closes a session. An open cart that still has items blocks closing (the cashier must check out
/// or clear it first). An empty open cart is discarded implicitly — it is never reachable again
/// because carts can only be started on an open session.
/// </summary>
public sealed record ClosePosSessionCommand(Guid SessionId);

public sealed class ClosePosSessionCommandHandler(
    IPosSessionRepository sessionRepository,
    IPosCartRepository cartRepository,
    IPosUnitOfWork unitOfWork,
    IAuthorizationService authorization)
{
    public async Task<Result> HandleAsync(
        ClosePosSessionCommand command,
        CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(POS.Application.Security.POSCapabilities.ManageSession, cancellationToken);
        if (allowed.IsFailure) return allowed;

        var sessionId = new PosSessionId(command.SessionId);
        var session = await sessionRepository.GetByIdAsync(sessionId, cancellationToken);
        if (session is null)
            return Result.Failure(Error.NotFound(
                "POS.CloseSession.SessionNotFound",
                $"POS session '{command.SessionId}' was not found."));

        var openCart = await cartRepository.GetOpenCartForSessionAsync(sessionId, cancellationToken);
        if (openCart is not null && openCart.Items.Count > 0)
            return Result.Failure(Error.Conflict(
                "POS.CloseSession.OpenCartHasItems",
                "The session has an open cart with items. Check out or clear it before closing the session."));

        var closeResult = session.Close();
        if (closeResult.IsFailure)
            return closeResult;

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
