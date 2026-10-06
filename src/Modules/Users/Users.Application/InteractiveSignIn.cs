using Platform.Application.Abstractions.Authorization;
using Platform.Core.Results;
using Users.Application.Commands;
using Users.Application.Repositories;

namespace Users.Application.Security;

/// <summary>Which screen the desktop must show before anything else.</summary>
public enum SignInStage
{
    /// <summary>No user exists yet: the first administrator must be created.</summary>
    FirstRunSetup = 0,

    /// <summary>Users exist and nobody is signed in.</summary>
    SignIn = 1,

    /// <summary>Somebody is signed in.</summary>
    SignedIn = 2
}

public sealed record SignInStatus(SignInStage Stage, string? UserName, string? DisplayName);

/// <summary>
/// The whole interactive authentication flow of the desktop in one testable place, so the window that shows it (Client.Desktop) is only
/// glue: first-run setup (create the first administrator, then sign in), ordinary sign-in, the forced change of a temporary password, and
/// sign-out. Every step goes through the real handlers, so lockout, password policy, auditing and the "first run only while no user exists"
/// rule all apply. Nothing here is a shortcut around them.
/// </summary>
public sealed class InteractiveSignInService(
    IUserRepository users,
    ICurrentUser currentUser,
    SignInCommandHandler signIn,
    SignOutCommandHandler signOut,
    ChangePasswordCommandHandler changePassword,
    BootstrapAdministratorCommandHandler bootstrap)
{
    public const string ConfirmationMismatchCode = "Users.SignIn.ConfirmationMismatch";

    /// <summary>Reveals only whether any user exists and who is signed in: what the start screen needs, nothing more.</summary>
    public async Task<SignInStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (currentUser.IsAuthenticated)
            return new SignInStatus(SignInStage.SignedIn, currentUser.UserName, currentUser.DisplayName);

        return new SignInStatus(await users.AnyAsync(cancellationToken) ? SignInStage.SignIn : SignInStage.FirstRunSetup, null, null);
    }

    /// <summary>Creates the first administrator and signs them in. Refused for ever once any user exists.</summary>
    public async Task<Result<SignInOutcome>> CompleteFirstRunAsync(
        string username, string displayName, string password, string confirmation, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(password, confirmation, StringComparison.Ordinal))
            return Result.Failure<SignInOutcome>(Mismatch());

        var created = await bootstrap.HandleAsync(new BootstrapAdministratorCommand(username, displayName, password), cancellationToken);
        if (created.IsFailure) return Result.Failure<SignInOutcome>(created.Error);

        return await signIn.HandleAsync(new SignInCommand(username, password), cancellationToken);
    }

    /// <summary>
    /// Signs in. A failure with <see cref="UsersSecurityErrors.PasswordChangeRequiredCode"/> means the screen must ask for a new password and call
    /// <see cref="ChangePasswordAndSignInAsync"/>.
    /// </summary>
    public Task<Result<SignInOutcome>> SignInAsync(string username, string password, CancellationToken cancellationToken = default)
        => signIn.HandleAsync(new SignInCommand(username, password), cancellationToken);

    /// <summary>Replaces a (temporary) password - proving the current one again under the same lockout - and signs in with the new one.</summary>
    public async Task<Result<SignInOutcome>> ChangePasswordAndSignInAsync(
        string username, string currentPassword, string newPassword, string confirmation, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(newPassword, confirmation, StringComparison.Ordinal))
            return Result.Failure<SignInOutcome>(Mismatch());

        var changed = await changePassword.HandleAsync(new ChangePasswordCommand(username, currentPassword, newPassword), cancellationToken);
        if (changed.IsFailure) return Result.Failure<SignInOutcome>(changed.Error);

        return await signIn.HandleAsync(new SignInCommand(username, newPassword), cancellationToken);
    }

    public Task<Result> SignOutAsync(CancellationToken cancellationToken = default)
        => signOut.HandleAsync(new SignOutCommand(), cancellationToken);

    private static Error Mismatch() => Error.Validation(ConfirmationMismatchCode, "The two passwords do not match.");
}
