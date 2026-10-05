namespace Platform.Application.Abstractions.Authorization;

/// <summary>
/// The identity of whoever is operating the application right now (AUTHENTICATION only: "who is this?").
///
/// What that person may DO is a different question, answered by <see cref="IAuthorizationService"/>. Business logic depends on
/// these abstractions and never on a session, a Windows principal, HTTP claims or any specific authentication technology.
/// The identity is established by signing in (<see cref="ISessionManager"/>); nothing a caller passes in a command can change it.
/// </summary>
public interface ICurrentUser
{
    /// <summary>True once a user has signed in and until the session ends.</summary>
    bool IsAuthenticated { get; }

    /// <summary>The signed-in user's ID, or <see cref="Guid.Empty"/> when nobody is signed in.</summary>
    Guid UserId { get; }

    /// <summary>The signed-in user's login name, or an empty string when nobody is signed in.</summary>
    string UserName { get; }

    /// <summary>The signed-in user's display name, or an empty string when nobody is signed in.</summary>
    string DisplayName { get; }
}

/// <summary>The identity established by a successful sign-in. It carries NO permissions: those are always looked up live.</summary>
public sealed record AuthenticatedIdentity(Guid UserId, string UserName, string DisplayName);

/// <summary>
/// Starts and ends the application session. Only the sign-in/sign-out use cases call this; it exists so that "who is signed in"
/// has exactly one owner and one source of truth for the whole process.
/// </summary>
public interface ISessionManager
{
    void SignIn(AuthenticatedIdentity identity);

    void SignOut();
}
