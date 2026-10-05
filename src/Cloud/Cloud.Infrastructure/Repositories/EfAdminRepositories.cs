using AdminPortal.Application;
using Cloud.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cloud.Infrastructure.Repositories;

public sealed class EfCustomerRepository(IDbContextFactory<CloudDbContext> factory) : ICustomerRepository
{
    public async Task AddAsync(Customer customer, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.Customers.Add(customer);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveAsync(Customer customer, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.Customers.Update(customer);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<Customer?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    }

    public async Task<bool> NameExistsAsync(string name, Guid? excludingId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Customers.AnyAsync(c => c.Name == name && (excludingId == null || c.Id != excludingId), cancellationToken);
    }

    public async Task<CustomerPage> ListAsync(string? search, bool includeInactive, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = db.Customers.AsNoTracking().AsQueryable();

        if (!includeInactive) query = query.Where(c => c.IsActive);
        if (search is not null)
        {
            // LIKE (not Contains/instr) so the search is case-insensitive; the term is escaped so % and _ match literally.
            var pattern = "%" + search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(c => EF.Functions.Like(c.Name, pattern, "\\")
                                  || (c.Email != null && EF.Functions.Like(c.Email, pattern, "\\"))
                                  || (c.ContactName != null && EF.Functions.Like(c.ContactName, pattern, "\\")));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(c => c.Name).Skip((Math.Max(page, 1) - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new CustomerPage(items, total);
    }

    public async Task<int> CountAsync(bool? isActive, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Customers.CountAsync(c => isActive == null || c.IsActive == isActive, cancellationToken);
    }
}

public sealed class EfModuleRegistryRepository(IDbContextFactory<CloudDbContext> factory) : IModuleRegistryRepository
{
    public async Task AddAsync(RegisteredModule module, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.Modules.Add(module);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveAsync(RegisteredModule module, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.Modules.Update(module);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<RegisteredModule?> FindAsync(string moduleId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Modules.AsNoTracking().FirstOrDefaultAsync(m => m.ModuleId == moduleId, cancellationToken);
    }

    public async Task<IReadOnlyList<RegisteredModule>> FindManyAsync(IEnumerable<string> moduleIds, CancellationToken cancellationToken = default)
    {
        var ids = moduleIds.ToList();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Modules.AsNoTracking().Where(m => ids.Contains(m.ModuleId)).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RegisteredModule>> ListAsync(bool includeRetired, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Modules.AsNoTracking()
            .Where(m => includeRetired || m.IsActive)
            .OrderBy(m => m.ModuleId)
            .ToListAsync(cancellationToken);
    }
}

/// <summary>Append-only administrative audit log. There is no update or delete.</summary>
public sealed class EfAdminAuditLog(IDbContextFactory<CloudDbContext> factory) : IAdminAuditLog
{
    public async Task AppendAsync(AdminAuditEntry entry, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.AuditLog.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<AuditPage> QueryAsync(AuditFilter filter, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = db.AuditLog.AsNoTracking().AsQueryable();

        if (filter.Actor is not null) query = query.Where(e => e.Actor == filter.Actor);
        if (filter.Action is not null) query = query.Where(e => e.Action == filter.Action);
        if (filter.EntityType is not null) query = query.Where(e => e.EntityType == filter.EntityType);
        if (filter.EntityId is not null) query = query.Where(e => e.EntityId == filter.EntityId);
        if (filter.From is { } from) query = query.Where(e => e.At >= from);
        if (filter.To is { } to) query = query.Where(e => e.At <= to);

        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(e => e.At).ThenBy(e => e.Id)
            .Skip((Math.Max(page, 1) - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new AuditPage(items, total);
    }
}
