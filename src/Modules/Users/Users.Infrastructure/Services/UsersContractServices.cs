using Users.Application.Queries;
using Users.Application.Repositories;
using Users.Contracts.Interfaces;
using Users.Contracts.Models;
using Users.Domain.Entities;
using Users.Domain.Enums;
using Users.Domain.ValueObjects;

namespace Users.Infrastructure.Services;

/// <summary>Implements IUserLookup from Users.Contracts (read-only).</summary>
internal sealed class UserLookup(IUserRepository users) : IUserLookup
{
    public async Task<UserLookupResult?> FindByIdAsync(Guid userId, CancellationToken cancellationToken = default)
        => userId == Guid.Empty ? null : ToResult(await users.GetByIdAsync(new UserId(userId), cancellationToken));

    public async Task<UserLookupResult?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        var normalized = User.NormalizeUsername(username);
        return normalized.IsFailure ? null : ToResult(await users.GetByUsernameAsync(normalized.Value, cancellationToken));
    }

    private static UserLookupResult? ToResult(User? u)
        => u is null ? null : new UserLookupResult(u.Id.Value, u.Username, u.DisplayName, u.Status == UserStatus.Active);
}

/// <summary>Implements IUserPermissionChecker from Users.Contracts (read-only).</summary>
internal sealed class UserPermissionChecker(GetUserPermissionsQueryHandler handler) : IUserPermissionChecker
{
    public async Task<bool> HasPermissionAsync(Guid userId, string permission, CancellationToken cancellationToken = default)
    {
        var code = PermissionCode.Create(permission);
        if (code.IsFailure || userId == Guid.Empty) return false;

        return (await GetPermissionsAsync(userId, cancellationToken)).Contains(code.Value.Value);
    }

    public async Task<IReadOnlyList<string>> GetPermissionsAsync(Guid userId, CancellationToken cancellationToken = default)
        => userId == Guid.Empty ? [] : await handler.HandleAsync(new GetUserPermissionsQuery(userId), cancellationToken);
}
