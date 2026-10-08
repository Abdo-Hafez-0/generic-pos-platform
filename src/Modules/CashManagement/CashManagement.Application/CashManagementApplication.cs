using Platform.Application.Abstractions.Authorization;
using CashManagement.Domain.Entities;
using CashManagement.Domain.Enums;
using CashManagement.Domain.ValueObjects;
using Platform.Core.Results;

namespace CashManagement.Application.Abstractions
{
    /// <summary>Saves changes to the CashManagement module's own persistence (CashManagementDbContext).</summary>
    public interface ICashManagementUnitOfWork
    {
        Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    }
}

namespace CashManagement.Application.Repositories
{
    public interface ICashSessionRepository
    {
        /// <summary>Loads the session with its movements.</summary>
        Task<CashSession?> GetByIdAsync(CashSessionId id, CancellationToken cancellationToken = default);

        /// <summary>The open session of the drawer (code already upper-case), with its movements, or null.</summary>
        Task<CashSession?> GetOpenByDrawerAsync(string drawerCode, CancellationToken cancellationToken = default);

        Task AddAsync(CashSession session, CancellationToken cancellationToken = default);

        Task<(IReadOnlyList<CashSession> Items, int TotalCount)> ListAsync(
            string? drawerCode, CashSessionStatus? status, int skip, int take, CancellationToken cancellationToken = default);
    }
}

namespace CashManagement.Application.DTOs
{
    public sealed record CashMovementDto(
        Guid MovementId, CashMovementKind Kind, decimal Amount, decimal SignedAmount, string? Reason,
        string? ReferenceType, Guid? ReferenceId, string? RecordedBy, DateTime RecordedAt);

    public sealed record CashSessionDto(
        Guid SessionId, string DrawerCode, string OpenedBy, decimal OpeningFloat, decimal Balance, CashSessionStatus Status,
        DateTime OpenedAt, DateTime? ClosedAt, string? ClosedBy, decimal? CountedAmount, decimal? ExpectedAmount, decimal? Variance,
        string? Notes, IReadOnlyList<CashMovementDto> Movements);

    public sealed record CashSessionSummaryDto(
        Guid SessionId, string DrawerCode, string OpenedBy, CashSessionStatus Status, DateTime OpenedAt, DateTime? ClosedAt, decimal? Variance);

    public sealed record PagedCashSessions(IReadOnlyList<CashSessionSummaryDto> Items, int TotalCount, int Page, int PageSize);
}

namespace CashManagement.Application.Commands
{
    using CashManagement.Application.Abstractions;
    using CashManagement.Application.Repositories;

    /// <summary>
    /// Who did it: the SIGNED-IN user whenever someone is signed in, whatever name the caller passed (the same rule as the POS till session,
    /// Stage 11), so a drawer can never be opened, counted or paid out in someone else's name. The supplied name is used only by hosts without
    /// authentication.
    /// </summary>
    /// <summary>The cash entries of the audit log (FIX-05): recorded after the commit, best effort.</summary>
    internal static class CashAudit
    {
        public static string Money(decimal amount) => amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

        public static Task RecordAsync(Platform.Application.Abstractions.Auditing.IBusinessEventSink? sink, string action, Guid sessionId, string summary, string? details = null)
            => Platform.Application.Abstractions.Auditing.BusinessEventSinkExtensions.TryRecordAsync(sink,
                Platform.Application.Abstractions.Auditing.BusinessEvent.Create("cash-management", action, "cash-session", sessionId.ToString(), summary, details));
    }

    internal static class CashActor
    {
        public static string? Resolve(ICurrentUser? currentUser, string? supplied)
            => currentUser is { IsAuthenticated: true } ? currentUser.UserName : supplied;
    }

    /// <summary>Opens a shift for a drawer. A drawer can have only one open session.</summary>
    public sealed record OpenCashSessionCommand(string DrawerCode, string OpenedBy, decimal OpeningFloat, string? Notes = null);

    public sealed class OpenCashSessionCommandHandler(ICashSessionRepository sessions, ICashManagementUnitOfWork unitOfWork, IAuthorizationService authorization, ICurrentUser? currentUser = null,
        Platform.Application.Abstractions.Auditing.IBusinessEventSink? businessEvents = null)
    {
        public async Task<Result<Guid>> HandleAsync(OpenCashSessionCommand command, CancellationToken cancellationToken = default)
        {
            var allowed = await authorization.AuthorizeAsync(CashManagement.Application.Security.CashManagementCapabilities.ManageSessions, cancellationToken);
            if (allowed.IsFailure) return Result.Failure<Guid>(allowed.Error);

            var created = CashSession.Open(command.DrawerCode, CashActor.Resolve(currentUser, command.OpenedBy)!, command.OpeningFloat, command.Notes);
            if (created.IsFailure) return Result.Failure<Guid>(created.Error);

            if (await sessions.GetOpenByDrawerAsync(created.Value.DrawerCode, cancellationToken) is not null)
                return Result.Failure<Guid>(Error.Conflict(
                    "CashManagement.OpenSession.AlreadyOpen", $"Drawer '{created.Value.DrawerCode}' already has an open session."));

            await sessions.AddAsync(created.Value, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await CashAudit.RecordAsync(businessEvents, "cash.shift-opened", created.Value.Id.Value,   // FIX-05
                $"Drawer {created.Value.DrawerCode} opened with a float of {CashAudit.Money(command.OpeningFloat)}.");
            return Result.Success(created.Value.Id.Value);
        }
    }

    public sealed record RecordCashMovementCommand(
        Guid SessionId, CashMovementKind Kind, decimal Amount, string? Reason = null,
        string? ReferenceType = null, Guid? ReferenceId = null, string? RecordedBy = null);

    public sealed record RecordedCashMovement(Guid MovementId, decimal BalanceAfter);

    public sealed class RecordCashMovementCommandHandler(ICashSessionRepository sessions, ICashManagementUnitOfWork unitOfWork, IAuthorizationService authorization, ICurrentUser? currentUser = null,
        Platform.Application.Abstractions.Auditing.IBusinessEventSink? businessEvents = null)
    {
        public async Task<Result<RecordedCashMovement>> HandleAsync(RecordCashMovementCommand command, CancellationToken cancellationToken = default)
        {
            var allowed = await authorization.AuthorizeAsync(CashManagement.Application.Security.CashManagementCapabilities.RecordMovement, cancellationToken);
            if (allowed.IsFailure) return Result.Failure<RecordedCashMovement>(allowed.Error);

            // FIX-05: a movement made at the drawer (pay-in, pay-out) is audited here. Cash sales come through the contract (ExecuteAsync)
            // inside the checkout transaction and are covered by the POS "sale.completed" entry.
            var recorded = await ExecuteAsync(command, cancellationToken);
            if (recorded.IsSuccess)
            {
                var kind = command.Kind switch
                {
                    CashMovementKind.PayIn => "pay-in", CashMovementKind.PayOut => "pay-out",
                    CashMovementKind.CashSale => "cash-sale", _ => "cash-refund"
                };
                await CashAudit.RecordAsync(businessEvents, "cash." + kind, command.SessionId,
                    $"{kind} of {CashAudit.Money(command.Amount)}{(string.IsNullOrWhiteSpace(command.Reason) ? string.Empty : ": " + command.Reason)}; balance {CashAudit.Money(recorded.Value.BalanceAfter)}.",
                    $"movement={recorded.Value.MovementId}");
            }

            return recorded;
        }

        /// <summary>The same operation WITHOUT the capability check, for trusted calls from other modules through this module's
        /// contracts (they run inside an operation the user was already authorized for). Not reachable from UI or other modules.</summary>
        internal async Task<Result<RecordedCashMovement>> ExecuteAsync(RecordCashMovementCommand command, CancellationToken cancellationToken = default)
        {
            var session = await sessions.GetByIdAsync(new CashSessionId(command.SessionId), cancellationToken);
            if (session is null)
                return Result.Failure<RecordedCashMovement>(Error.NotFound(
                    "CashManagement.RecordMovement.SessionNotFound", $"Cash session '{command.SessionId}' was not found."));

            var recorded = session.RecordMovement(
                command.Kind, command.Amount, command.Reason, command.ReferenceType, command.ReferenceId, CashActor.Resolve(currentUser, command.RecordedBy));
            if (recorded.IsFailure) return Result.Failure<RecordedCashMovement>(recorded.Error);

            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Success(new RecordedCashMovement(recorded.Value.Id.Value, session.Balance));
        }
    }

    /// <summary>Closes a shift with the counted amount; the variance against the expected balance is stored.</summary>
    public sealed record CloseCashSessionCommand(Guid SessionId, decimal CountedAmount, string ClosedBy, string? Notes = null);

    public sealed record ClosedCashSession(decimal ExpectedAmount, decimal CountedAmount, decimal Variance);

    public sealed class CloseCashSessionCommandHandler(ICashSessionRepository sessions, ICashManagementUnitOfWork unitOfWork, IAuthorizationService authorization, ICurrentUser? currentUser = null,
        Platform.Application.Abstractions.Auditing.IBusinessEventSink? businessEvents = null)
    {
        public async Task<Result<ClosedCashSession>> HandleAsync(CloseCashSessionCommand command, CancellationToken cancellationToken = default)
        {
            var allowed = await authorization.AuthorizeAsync(CashManagement.Application.Security.CashManagementCapabilities.ManageSessions, cancellationToken);
            if (allowed.IsFailure) return Result.Failure<ClosedCashSession>(allowed.Error);

            var session = await sessions.GetByIdAsync(new CashSessionId(command.SessionId), cancellationToken);
            if (session is null)
                return Result.Failure<ClosedCashSession>(Error.NotFound(
                    "CashManagement.CloseSession.SessionNotFound", $"Cash session '{command.SessionId}' was not found."));

            var closed = session.Close(command.CountedAmount, CashActor.Resolve(currentUser, command.ClosedBy)!, command.Notes);
            if (closed.IsFailure) return Result.Failure<ClosedCashSession>(closed.Error);

            await unitOfWork.SaveChangesAsync(cancellationToken);
            var outcome = new ClosedCashSession(session.ExpectedAmount!.Value.Value, session.CountedAmount!.Value.Value, session.Variance!.Value);
            await CashAudit.RecordAsync(businessEvents, "cash.shift-closed", command.SessionId,   // FIX-05
                $"Drawer {session.DrawerCode} closed: expected {CashAudit.Money(outcome.ExpectedAmount)}, counted {CashAudit.Money(outcome.CountedAmount)}, difference {CashAudit.Money(outcome.Variance)}.");
            return Result.Success(outcome);
        }
    }
}

namespace CashManagement.Application.Queries
{
    using CashManagement.Application.DTOs;
    using CashManagement.Application.Repositories;

    internal static class CashMapping
    {
        public static CashSessionDto ToDto(this CashSession s) => new(
            s.Id.Value, s.DrawerCode, s.OpenedBy, s.OpeningFloat.Value, s.Balance, s.Status, s.OpenedAt, s.ClosedAt, s.ClosedBy,
            s.CountedAmount?.Value, s.ExpectedAmount?.Value, s.Variance, s.Notes,
            s.Movements.OrderBy(m => m.RecordedAt).Select(m => new CashMovementDto(
                m.Id.Value, m.Kind, m.Amount, m.SignedAmount, m.Reason, m.ReferenceType, m.ReferenceId, m.RecordedBy, m.RecordedAt)).ToList());
    }

    public sealed record GetCashSessionQuery(Guid SessionId);

    public sealed class GetCashSessionQueryHandler(ICashSessionRepository sessions)
    {
        public async Task<CashSessionDto?> HandleAsync(GetCashSessionQuery query, CancellationToken cancellationToken = default)
            => (await sessions.GetByIdAsync(new CashSessionId(query.SessionId), cancellationToken))?.ToDto();
    }

    public sealed record GetOpenCashSessionQuery(string DrawerCode);

    public sealed class GetOpenCashSessionQueryHandler(ICashSessionRepository sessions)
    {
        public async Task<CashSessionDto?> HandleAsync(GetOpenCashSessionQuery query, CancellationToken cancellationToken = default)
            => string.IsNullOrWhiteSpace(query.DrawerCode)
                ? null
                : (await sessions.GetOpenByDrawerAsync(query.DrawerCode.Trim().ToUpperInvariant(), cancellationToken))?.ToDto();
    }

    public sealed record ListCashSessionsQuery(string? DrawerCode = null, CashSessionStatus? Status = null, int Page = 1, int PageSize = 50);

    public sealed class ListCashSessionsQueryHandler(ICashSessionRepository sessions)
    {
        public const int MaxPageSize = 200;

        public async Task<PagedCashSessions> HandleAsync(ListCashSessionsQuery query, CancellationToken cancellationToken = default)
        {
            var page = Math.Max(1, query.Page);
            var size = Math.Clamp(query.PageSize, 1, MaxPageSize);
            var drawer = string.IsNullOrWhiteSpace(query.DrawerCode) ? null : query.DrawerCode.Trim().ToUpperInvariant();

            var (items, total) = await sessions.ListAsync(drawer, query.Status, (page - 1) * size, size, cancellationToken);
            return new PagedCashSessions(
                items.Select(s => new CashSessionSummaryDto(s.Id.Value, s.DrawerCode, s.OpenedBy, s.Status, s.OpenedAt, s.ClosedAt, s.Variance)).ToList(),
                total, page, size);
        }
    }
}
