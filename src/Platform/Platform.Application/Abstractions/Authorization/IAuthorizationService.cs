using Platform.Core.Results;

namespace Platform.Application.Abstractions.Authorization;

/// <summary>
/// The single place that answers "may the signed-in user do this right now?" (AUTHORIZATION).
///
/// Handlers call <see cref="AuthorizeAsync"/> BEFORE they do anything, so the rule holds no matter who calls them (a screen, a test,
/// a script): hiding a button is a convenience, never the control. The answer is always computed live - from the user's current
/// roles, not from anything cached at sign-in - so a role or capability change, or deactivating a user, takes effect immediately.
/// It fails closed: nobody signed in, an unknown capability, or an unavailable permission source all mean "no".
/// </summary>
public interface IAuthorizationService
{
    /// <summary>
    /// Succeeds when the user is signed in and holds <paramref name="capability"/>. Otherwise returns an
    /// <see cref="ErrorType.Unauthorized"/> failure (codes in <see cref="SecurityErrors"/>) and records a security event.
    /// </summary>
    Task<Result> AuthorizeAsync(string capability, CancellationToken cancellationToken = default);

    /// <summary>The same decision without side effects (no security event). For a UI that wants to hide or disable things.</summary>
    Task<bool> IsAllowedAsync(string capability, CancellationToken cancellationToken = default);
}

/// <summary>Supplies the permission codes a user currently holds (implemented by the Users module). Looked up on every decision.</summary>
public interface IPermissionProvider
{
    /// <summary>All distinct permission codes the user holds through their roles; empty for an unknown or inactive user.</summary>
    Task<IReadOnlyCollection<string>> GetPermissionsAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>Error codes of authorization failures.</summary>
public static class SecurityErrors
{
    public const string NotAuthenticatedCode = "Security.NotAuthenticated";
    public const string ForbiddenCode = "Security.Forbidden";
    public const string UnknownCapabilityCode = "Security.UnknownCapability";
    public const string LicenseRestrictedCode = "Security.LicenseRestricted";

    public static Error NotAuthenticated() => Error.Unauthorized(NotAuthenticatedCode, "Sign in to continue.");

    public static Error Forbidden(string capability) => Error.Unauthorized(ForbiddenCode, $"You do not have permission to do this ({capability}).");

    public static Error UnknownCapability(string capability) => Error.Unauthorized(UnknownCapabilityCode, $"The operation '{capability}' is not a known capability, so it is refused.");

    public static Error LicenseRestricted(string capability, string state) =>
        Error.Unauthorized(LicenseRestrictedCode, $"This feature is not available with the current license ({state}). Your data is safe and unchanged.");
}
