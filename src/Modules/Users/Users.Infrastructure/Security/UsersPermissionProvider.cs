using Platform.Application.Abstractions.Authorization;
using Users.Application.Queries;

namespace Users.Infrastructure.Security;

/// <summary>
/// Answers the platform's authorization question from the Users data, LIVE on every call: a changed role, a revoked permission or a
/// deactivated user is reflected on the very next check (an inactive or unknown user holds nothing).
/// </summary>
internal sealed class UsersPermissionProvider(GetUserPermissionsQueryHandler handler) : IPermissionProvider
{
    public async Task<IReadOnlyCollection<string>> GetPermissionsAsync(Guid userId, CancellationToken cancellationToken = default)
        => userId == Guid.Empty ? [] : await handler.HandleAsync(new GetUserPermissionsQuery(userId), cancellationToken);
}
