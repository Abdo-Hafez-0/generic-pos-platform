using Audit.Application.Repositories;
using Audit.Domain.Entities;
using Audit.Domain.ValueObjects;
using Audit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Audit.Infrastructure.Repositories;

internal sealed class EfAuditEntryRepository(AuditDbContext dbContext) : IAuditEntryRepository
{
    public async Task AddAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        => await dbContext.AuditEntries.AddAsync(entry, cancellationToken);

    public async Task<AuditEntry?> GetByIdAsync(AuditEntryId id, CancellationToken cancellationToken = default)
        => await dbContext.AuditEntries.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    public async Task<(IReadOnlyList<AuditEntry> Items, int TotalCount)> QueryAsync(
        AuditFilter filter, int skip, int take, CancellationToken cancellationToken = default)
    {
        var query = dbContext.AuditEntries.AsNoTracking();

        if (filter.Module is { } module) query = query.Where(e => e.Module == module);
        if (filter.Action is { } action) query = query.Where(e => e.Action == action);
        if (filter.EntityType is { } entityType) query = query.Where(e => e.EntityType == entityType);
        if (filter.EntityId is { } entityId) query = query.Where(e => e.EntityId == entityId);
        if (filter.ActorId is { } actorId) query = query.Where(e => e.ActorId == actorId);
        if (filter.From is { } from) query = query.Where(e => e.OccurredAt >= from);
        if (filter.To is { } to) query = query.Where(e => e.OccurredAt <= to);

        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id)
            .Skip(skip).Take(take).ToListAsync(cancellationToken);
        return (items, total);
    }
}
