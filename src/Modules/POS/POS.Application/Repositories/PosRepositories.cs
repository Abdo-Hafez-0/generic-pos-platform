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

    /// <summary>MISS-07c: carts checked out since a moment (including items), newest first, at most <paramref name="max"/>.</summary>
    Task<IReadOnlyList<PosCart>> GetCheckedOutSinceAsync(DateTime sinceUtc, int max, CancellationToken cancellationToken = default);

    /// <summary>MISS-10: the checked-out cart that became the given sale (its till session tells which warehouse it was sold from), or null.</summary>
    Task<PosCart?> GetBySaleIdAsync(Guid saleId, CancellationToken cancellationToken = default);

    Task AddAsync(PosCart cart, CancellationToken cancellationToken = default);
}
