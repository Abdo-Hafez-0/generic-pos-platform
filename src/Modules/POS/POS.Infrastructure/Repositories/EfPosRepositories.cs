using Microsoft.EntityFrameworkCore;
using POS.Application.Repositories;
using POS.Domain.Entities;
using POS.Domain.Enums;
using POS.Domain.ValueObjects;
using POS.Infrastructure.Persistence;

namespace POS.Infrastructure.Repositories;

internal sealed class EfPosSessionRepository(POSDbContext dbContext) : IPosSessionRepository
{
    public async Task<PosSession?> GetByIdAsync(PosSessionId id, CancellationToken cancellationToken = default)
        => await dbContext.Sessions.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    public async Task<PosSession?> GetOpenSessionForCashierAsync(string cashierReference, CancellationToken cancellationToken = default)
    {
        // A cashier has very few open sessions (normally one); order in memory like the cart lookup below.
        var sessions = await dbContext.Sessions
            .Where(s => s.CashierReference == cashierReference && s.Status == PosSessionStatus.Open)
            .ToListAsync(cancellationToken);

        return sessions.OrderByDescending(s => s.OpenedAt).FirstOrDefault();
    }

    public async Task AddAsync(PosSession session, CancellationToken cancellationToken = default)
        => await dbContext.Sessions.AddAsync(session, cancellationToken);
}

internal sealed class EfPosCartRepository(POSDbContext dbContext) : IPosCartRepository
{
    public async Task<PosCart?> GetByIdAsync(PosCartId id, CancellationToken cancellationToken = default)
        => await dbContext.Carts
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public async Task<PosCart?> GetOpenCartForSessionAsync(PosSessionId sessionId, CancellationToken cancellationToken = default)
    {
        // SQLite cannot ORDER BY a DateTime reliably through EF in every case; load the (tiny) set and order in memory.
        var carts = await dbContext.Carts
            .Include(c => c.Items)
            .Where(c => c.SessionId == sessionId && c.Status == PosCartStatus.Open)
            .ToListAsync(cancellationToken);

        return carts.OrderByDescending(c => c.CreatedAt).FirstOrDefault();
    }

    public async Task<IReadOnlyList<PosCart>> GetCheckedOutSinceAsync(DateTime sinceUtc, int max, CancellationToken cancellationToken = default)
    {
        // read-only (no tracking); ordered in memory like the lookups above
        var carts = await dbContext.Carts.AsNoTracking()
            .Include(c => c.Items)
            .Where(c => c.Status == PosCartStatus.CheckedOut && c.CheckedOutAt >= sinceUtc)
            .ToListAsync(cancellationToken);

        return carts.OrderByDescending(c => c.CheckedOutAt).Take(max).ToList();
    }

    public async Task AddAsync(PosCart cart, CancellationToken cancellationToken = default)
        => await dbContext.Carts.AddAsync(cart, cancellationToken);

    public async Task<PosCart?> GetBySaleIdAsync(Guid saleId, CancellationToken cancellationToken = default)
        => await dbContext.Carts.AsNoTracking()
            .FirstOrDefaultAsync(c => c.SaleId == saleId && c.Status == PosCartStatus.CheckedOut, cancellationToken);
}
