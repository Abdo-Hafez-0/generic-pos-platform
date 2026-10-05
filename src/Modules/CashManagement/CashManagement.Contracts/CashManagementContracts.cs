namespace CashManagement.Contracts.Models
{
    public enum CashMovementKindContract
    {
        PayIn = 1,
        PayOut = 2,
        CashSale = 3,
        CashRefund = 4
    }

    /// <summary>Read model of a cash drawer session. Never exposes CashManagement.Domain types.</summary>
    public sealed record CashSessionResult(
        Guid SessionId,
        string DrawerCode,
        string OpenedBy,
        decimal OpeningFloat,
        decimal Balance,
        bool IsOpen,
        DateTime OpenedAt);

    /// <summary>
    /// A request to record cash going in or out of an open session. Reason is required for PayIn/PayOut. A reference (type + ID, e.g. "sale" + sale ID)
    /// makes the call idempotent per kind: the same reference is rejected with CashManagement.Movement.DuplicateReference.
    /// </summary>
    public sealed record RecordCashMovementRequest(
        Guid SessionId,
        CashMovementKindContract Kind,
        decimal Amount,
        string? Reason = null,
        string? ReferenceType = null,
        Guid? ReferenceId = null,
        string? RecordedBy = null);

    public sealed record RecordCashMovementResult(bool IsSuccess, Guid MovementId, decimal BalanceAfter, string? ErrorCode, string? ErrorMessage)
    {
        public static RecordCashMovementResult Success(Guid movementId, decimal balanceAfter) => new(true, movementId, balanceAfter, null, null);

        public static RecordCashMovementResult Failure(string errorCode, string errorMessage) => new(false, Guid.Empty, 0m, errorCode, errorMessage);
    }
}

namespace CashManagement.Contracts.Interfaces
{
    using CashManagement.Contracts.Models;

    /// <summary>
    /// Lets other modules (a future POS/Payments integration) record cash in the drawer without knowing how sessions are stored.
    /// Implemented by CashManagement.Infrastructure.Services.CashMovementRecorder. Fully offline.
    /// </summary>
    public interface ICashMovementRecorder
    {
        Task<RecordCashMovementResult> RecordMovementAsync(RecordCashMovementRequest request, CancellationToken cancellationToken = default);
    }

    /// <summary>Read-only access to cash sessions. Implemented by CashManagement.Infrastructure.Services.CashSessionReader.</summary>
    public interface ICashSessionReader
    {
        /// <summary>The open session of a drawer (case-insensitive), or null.</summary>
        Task<CashSessionResult?> GetOpenSessionAsync(string drawerCode, CancellationToken cancellationToken = default);

        Task<CashSessionResult?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
    }
}
