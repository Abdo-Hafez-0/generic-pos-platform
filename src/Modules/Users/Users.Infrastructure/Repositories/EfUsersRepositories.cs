using Microsoft.EntityFrameworkCore;
using Users.Application.Repositories;
using Users.Domain.Entities;
using Users.Domain.Enums;
using Users.Domain.ValueObjects;
using Users.Infrastructure.Persistence;

namespace Users.Infrastructure.Repositories;

internal sealed class EfUserRepository(UsersDbContext dbContext) : IUserRepository
{
    public async Task<User?> GetByIdAsync(UserId id, CancellationToken cancellationToken = default)
        => await dbContext.Users.Include(u => u.Roles).FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    public async Task<User?> GetByUsernameAsync(string normalizedUsername, CancellationToken cancellationToken = default)
        => await dbContext.Users.Include(u => u.Roles).FirstOrDefaultAsync(u => u.Username == normalizedUsername, cancellationToken);

    public async Task AddAsync(User user, CancellationToken cancellationToken = default)
        => await dbContext.Users.AddAsync(user, cancellationToken);

    public async Task<bool> AnyAsync(CancellationToken cancellationToken = default)
        => await dbContext.Users.AnyAsync(cancellationToken);

    public async Task<IReadOnlyList<(Guid UserId, Guid RoleId)>> GetActiveUserRolesAsync(CancellationToken cancellationToken = default)
    {
        // active users with their assignments (a shop has few users; the projection is done in memory because the SQLite provider
        // cannot translate a SelectMany over the owned role collection)
        var active = await dbContext.Users.AsNoTracking()
            .Include(u => u.Roles)
            .Where(u => u.Status == UserStatus.Active)
            .ToListAsync(cancellationToken);
        return active.SelectMany(u => u.Roles.Select(r => (u.Id.Value, r.RoleId.Value))).ToList();
    }

    public async Task<(IReadOnlyList<User> Items, int TotalCount)> ListAsync(
        string? search, bool includeInactive, int skip, int take, CancellationToken cancellationToken = default)
    {
        var query = dbContext.Users.AsNoTracking();
        if (!includeInactive) query = query.Where(u => u.Status == UserStatus.Active);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{Escape(search.Trim())}%";
            query = query.Where(u => EF.Functions.Like(u.Username, pattern, "\\") || EF.Functions.Like(u.DisplayName, pattern, "\\"));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(u => u.Username).Skip(skip).Take(take).ToListAsync(cancellationToken);
        return (items, total);
    }

    private static string Escape(string text)
        => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}

internal sealed class EfRoleRepository(UsersDbContext dbContext) : IRoleRepository
{
    public async Task<Role?> GetByIdAsync(RoleId id, CancellationToken cancellationToken = default)
        => await dbContext.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task<Role?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        // Role names are unique ignoring case; the default SQLite comparison is case-sensitive, so compare in lower case.
        var lowered = name.Trim().ToLower();
        return await dbContext.Roles.FirstOrDefaultAsync(r => r.Name.ToLower() == lowered, cancellationToken);
    }

    public async Task AddAsync(Role role, CancellationToken cancellationToken = default)
        => await dbContext.Roles.AddAsync(role, cancellationToken);

    public async Task<IReadOnlyList<Role>> ListAsync(CancellationToken cancellationToken = default)
        => await dbContext.Roles.AsNoTracking().Include(r => r.Permissions).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Role>> GetByIdsAsync(IReadOnlyCollection<RoleId> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0) return [];
        var wanted = ids.ToList();
        return await dbContext.Roles.AsNoTracking().Include(r => r.Permissions).Where(r => wanted.Contains(r.Id)).ToListAsync(cancellationToken);
    }
}

internal sealed class EfUserCredentialRepository(UsersDbContext dbContext) : IUserCredentialRepository
{
    public async Task<UserCredential?> GetByUserIdAsync(UserId userId, CancellationToken cancellationToken = default)
        => await dbContext.Credentials.FirstOrDefaultAsync(c => c.UserId == userId, cancellationToken);

    public async Task AddAsync(UserCredential credential, CancellationToken cancellationToken = default)
        => await dbContext.Credentials.AddAsync(credential, cancellationToken);
}
