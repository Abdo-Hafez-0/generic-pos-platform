using POS.Domain.Entities;
using POS.Domain.ValueObjects;

namespace POS.Application.Repositories;

public interface IPosSessionRepository
{
    Task<PosSession?> GetByIdAsync(PosSessionId id, CancellationToken cancellationToken = default);

    /// <summary>The most recently opened session of this cashier that is still open, or null.</summary>
    Task<PosSession?> GetOpenSessionForCashierAsync(string cashierReference, CancellationToken cancellationToken = default);
    Task AddAsync(PosSession session, CancellationToken cancellationToken = default);
}

public interface IPosCartRepository
{
    /// <summary>Loads a cart including its items.</summary>
    Task<PosCart?> GetByIdAsync(PosCartId id, CancellationToken cancellationToken = default);

    /// <summary>Returns the session's open cart (including items), or null.</summary>
    Task<PosCart?> GetOpenCartForSessionAsync(PosSessionId sessionId, CancellationToken cancellationToken = default);

    Task AddAsync(PosCart cart, CancellationToken cancellationToken = default);
}
