using CashManagement.Application.Repositories;
using CashManagement.Domain.Entities;
using CashManagement.Domain.Enums;
using CashManagement.Domain.ValueObjects;
using CashManagement.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CashManagement.Infrastructure.Repositories;

internal sealed class EfCashSessionRepository(CashManagementDbContext dbContext) : ICashSessionRepository
{
    public async Task<CashSession?> GetByIdAsync(CashSessionId id, CancellationToken cancellationToken = default, bool includeMovements = true)
        => await Sessions(includeMovements).FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    public async Task<CashSession?> GetOpenByDrawerAsync(string drawerCode, CancellationToken cancellationToken = default, bool includeMovements = true)
        => await Sessions(includeMovements).FirstOrDefaultAsync(s => s.DrawerCode == drawerCode && s.Status == CashSessionStatus.Open, cancellationToken);

    public Task<bool> HasMovementAsync(CashSessionId sessionId, CashMovementKind kind, string referenceType, Guid referenceId, CancellationToken cancellationToken = default)
        => dbContext.Set<CashMovement>().AnyAsync(m => m.SessionId == sessionId && m.Kind == kind && m.ReferenceType == referenceType && m.ReferenceId == referenceId, cancellationToken);

    private IQueryable<CashSession> Sessions(bool includeMovements)
        => includeMovements ? dbContext.CashSessions.Include(s => s.Movements) : dbContext.CashSessions;

    public async Task AddAsync(CashSession session, CancellationToken cancellationToken = default)
        => await dbContext.CashSessions.AddAsync(session, cancellationToken);

    public async Task<(IReadOnlyList<CashSession> Items, int TotalCount)> ListAsync(
        string? drawerCode, CashSessionStatus? status, int skip, int take, CancellationToken cancellationToken = default)
    {
        var query = dbContext.CashSessions.AsNoTracking();
        if (drawerCode is not null) query = query.Where(s => s.DrawerCode == drawerCode);
        if (status is { } st) query = query.Where(s => s.Status == st);

        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(s => s.OpenedAt).Skip(skip).Take(take).ToListAsync(cancellationToken);
        return (items, total);
    }
}
