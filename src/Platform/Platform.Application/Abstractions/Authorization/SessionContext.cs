namespace Platform.Application.Abstractions.Authorization;

/// <summary>
/// The one process-wide session of the desktop application: implements both the read side (<see cref="ICurrentUser"/>) and the write
/// side (<see cref="ISessionManager"/>). Holds identity only - never a password, hash or permission list.
/// </summary>
public sealed class SessionContext : ICurrentUser, ISessionManager
{
    private volatile AuthenticatedIdentity? _identity;

    public bool IsAuthenticated => _identity is not null;

    public Guid UserId => _identity?.UserId ?? Guid.Empty;

    public string UserName => _identity?.UserName ?? string.Empty;

    public string DisplayName => _identity?.DisplayName ?? string.Empty;

    public void SignIn(AuthenticatedIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.UserId == Guid.Empty)
            throw new ArgumentException("A signed-in user must have an ID.", nameof(identity));

        _identity = identity;
    }

    public void SignOut() => _identity = null;
}
