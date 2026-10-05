using Platform.Application.Abstractions.Authorization;
using Platform.Application.Abstractions.Security;
using Platform.Core.Results;
using Users.Application.Abstractions;
using Users.Application.Repositories;
using Users.Domain.Entities;
using Users.Domain.ValueObjects;

namespace Users.Application.Security
{
    /// <summary>The capabilities Users owns. Users and licensing administration stay available in every license state (never lock the owner out).</summary>
    public static class UsersCapabilities
    {
        public const string Module = "users";

        public const string View = "users.view";
        public const string Manage = "users.manage";

        public static IReadOnlyList<CapabilityDescriptor> All { get; } =
        [
            new(View, Module, "View users and roles", "List users, roles and the permissions they hold.", LicenseRequirement.None),
            new(Manage, Module, "Manage users", "Create, change, deactivate users; assign roles; grant permissions; reset passwords.", LicenseRequirement.None, IsSensitive: true)
        ];
    }

    public sealed class UsersCapabilityProvider : ICapabilityProvider
    {
        public IReadOnlyCollection<CapabilityDescriptor> GetCapabilities() => UsersCapabilities.All;
    }

    /// <summary>Password and lockout rules in force (from configuration, never weaker than the built-in floors).</summary>
    public sealed record UsersSecurityOptions(PasswordPolicy Passwords, LockoutPolicy Lockout)
    {
        public static UsersSecurityOptions Default { get; } = new(PasswordPolicy.Default, LockoutPolicy.Default);

        public UsersSecurityOptions Normalized() => new(Passwords.Normalized(), Lockout.Normalized());
    }

    public enum PasswordVerification
    {
        Failed = 0,
        Success = 1,

        /// <summary>The password is right but the stored hash uses an older/weaker cost: store a fresh hash.</summary>
        SuccessRehashNeeded = 2
    }

    /// <summary>
    /// One-way password hashing (implemented in Users.Infrastructure with a standard, salted, slow key-derivation function).
    /// The hash string is self-describing, so the cost can be raised later without invalidating existing passwords.
    /// </summary>
    public interface IPasswordHasher
    {
        /// <summary>A new salted hash. A different salt is used on every call, so hashing the same password twice differs.</summary>
        string Hash(string password);

        /// <summary>Constant-time check of a password against a stored hash. A malformed hash is simply a failed verification.</summary>
        PasswordVerification Verify(string password, string passwordHash);

        /// <summary>A valid hash of an unknowable password, verified against when the user does not exist so that timing does not reveal it.</summary>
        string DummyHash { get; }
    }

    public static class UsersSecurityErrors
    {
        public const string InvalidCredentialsCode = "Users.SignIn.InvalidCredentials";
        public const string LockedOutCode = "Users.SignIn.LockedOut";
        public const string AccountDisabledCode = "Users.SignIn.AccountDisabled";
        public const string PasswordChangeRequiredCode = "Users.SignIn.PasswordChangeRequired";

        /// <summary>The same answer for an unknown user, a user without a password and a wrong password.</summary>
        public static Error InvalidCredentials() => Error.Unauthorized(InvalidCredentialsCode, "The username or password is incorrect.");

        public static Error LockedOut(TimeSpan remaining)
        {
            var minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
            return Error.Unauthorized(LockedOutCode, $"Too many failed attempts. Try again in {minutes} minute(s).");
        }

        public static Error AccountDisabled() => Error.Unauthorized(AccountDisabledCode, "This account is disabled. Contact an administrator.");

        public static Error PasswordChangeRequired() => Error.Unauthorized(PasswordChangeRequiredCode, "You must choose a new password before signing in.");
    }

    /// <summary>
    /// Verifies a username/password pair with the lockout rules. Shared by sign-in and password change so that neither can be used to
    /// guess passwords around the other. Unknown user, missing password and wrong password are indistinguishable to the caller, and the
    /// same amount of hashing work is done for all three.
    /// </summary>
    internal sealed class CredentialVerifier(
        IUserRepository users,
        IUserCredentialRepository credentials,
        IPasswordHasher hasher,
        IUsersUnitOfWork unitOfWork,
        UsersSecurityOptions options,
        ISecurityEventSink? events,
        TimeProvider timeProvider)
    {
        public const int MaxInputLength = 256;

        internal sealed record Verified(User User, UserCredential Credential, PasswordVerification Verification);

        public async Task<Result<Verified>> VerifyAsync(string? username, string? password, string purpose, CancellationToken cancellationToken)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;

            // Malformed input: refuse before any hashing (no work for an oversized or empty value) with the generic answer.
            if (string.IsNullOrEmpty(password) || password.Length > MaxInputLength
                || string.IsNullOrWhiteSpace(username) || username.Length > MaxInputLength)
                return Result.Failure<Verified>(UsersSecurityErrors.InvalidCredentials());

            var normalized = User.NormalizeUsername(username);
            var user = normalized.IsSuccess ? await users.GetByUsernameAsync(normalized.Value, cancellationToken) : null;
            var credential = user is null ? null : await credentials.GetByUserIdAsync(user.Id, cancellationToken);

            if (user is null || credential is null)
            {
                hasher.Verify(password, hasher.DummyHash);
                // The typed name is only recorded when it is a real user: someone may have typed a password into the username box.
                await events.TryRecordAsync(SecurityEvent.Create(
                    "security.signin.failed", SecurityEventOutcome.Failure,
                    actorId: user?.Id.Value, actorName: user?.Username, subjectType: "user", subjectId: user?.Id.ToString(),
                    summary: $"{purpose}: unknown user or no password set"), cancellationToken);
                return Result.Failure<Verified>(UsersSecurityErrors.InvalidCredentials());
            }

            if (credential.IsLocked(now))
            {
                await events.TryRecordAsync(SecurityEvent.Create(
                    "security.signin.blocked", SecurityEventOutcome.Denied,
                    actorId: user.Id.Value, actorName: user.Username, subjectType: "user", subjectId: user.Id.ToString(),
                    summary: $"{purpose}: account is locked"), cancellationToken);
                return Result.Failure<Verified>(UsersSecurityErrors.LockedOut(credential.LockedUntil!.Value - now));
            }

            var verification = hasher.Verify(password, credential.PasswordHash);
            if (verification == PasswordVerification.Failed)
            {
                var locked = credential.RegisterFailure(now, options.Lockout);
                await unitOfWork.SaveChangesAsync(cancellationToken);

                await events.TryRecordAsync(SecurityEvent.Create(
                    locked ? "security.signin.locked" : "security.signin.failed", SecurityEventOutcome.Failure,
                    actorId: user.Id.Value, actorName: user.Username, subjectType: "user", subjectId: user.Id.ToString(),
                    summary: locked ? $"{purpose}: wrong password, account locked" : $"{purpose}: wrong password"), cancellationToken);

                return Result.Failure<Verified>(locked
                    ? UsersSecurityErrors.LockedOut(credential.LockedUntil!.Value - now)
                    : UsersSecurityErrors.InvalidCredentials());
            }

            return Result.Success(new Verified(user, credential, verification));
        }
    }
}

namespace Users.Application.Commands
{
    using Users.Application.Security;

    // ---------------------------------------------------------------- sign in / out

    /// <summary>Verifies the credentials and, only if they are right, starts the application session.</summary>
    public sealed record SignInCommand(string Username, string Password);

    public sealed record SignInOutcome(Guid UserId, string Username, string DisplayName);

    public sealed class SignInCommandHandler(
        IUserRepository users,
        IUserCredentialRepository credentials,
        IPasswordHasher hasher,
        IUsersUnitOfWork unitOfWork,
        ISessionManager session,
        UsersSecurityOptions options,
        ISecurityEventSink? events = null,
        TimeProvider? timeProvider = null)
    {
        private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

        public async Task<Result<SignInOutcome>> HandleAsync(SignInCommand command, CancellationToken cancellationToken = default)
        {
            var verifier = new CredentialVerifier(users, credentials, hasher, unitOfWork, options, events, _time);
            var verified = await verifier.VerifyAsync(command.Username, command.Password, "sign-in", cancellationToken);
            if (verified.IsFailure) return Result.Failure<SignInOutcome>(verified.Error);

            var (user, credential, verification) = verified.Value;

            // The password is right, so naming the reason is safe: only the account's owner can get here.
            if (user.Status != Domain.Enums.UserStatus.Active)
            {
                await events.TryRecordAsync(SecurityEvent.Create(
                    "security.signin.failed", SecurityEventOutcome.Failure, user.Id.Value, user.Username, "user", user.Id.ToString(),
                    "sign-in: account is disabled"), cancellationToken);
                return Result.Failure<SignInOutcome>(UsersSecurityErrors.AccountDisabled());
            }

            if (credential.MustChangePassword)
            {
                credential.Unlock();
                await unitOfWork.SaveChangesAsync(cancellationToken);
                await events.TryRecordAsync(SecurityEvent.Create(
                    "security.signin.failed", SecurityEventOutcome.Failure, user.Id.Value, user.Username, "user", user.Id.ToString(),
                    "sign-in: password change required"), cancellationToken);
                return Result.Failure<SignInOutcome>(UsersSecurityErrors.PasswordChangeRequired());
            }

            credential.RegisterSuccess(_time.GetUtcNow().UtcDateTime);
            if (verification == PasswordVerification.SuccessRehashNeeded)
                credential.UpgradeHash(hasher.Hash(command.Password));

            await unitOfWork.SaveChangesAsync(cancellationToken);

            session.SignIn(new AuthenticatedIdentity(user.Id.Value, user.Username, user.DisplayName));
            await events.TryRecordAsync(SecurityEvent.Create(
                "security.signin.succeeded", SecurityEventOutcome.Success, user.Id.Value, user.Username, "user", user.Id.ToString()), cancellationToken);

            return Result.Success(new SignInOutcome(user.Id.Value, user.Username, user.DisplayName));
        }
    }

    public sealed record SignOutCommand;

    public sealed class SignOutCommandHandler(ISessionManager session, ICurrentUser currentUser, ISecurityEventSink? events = null)
    {
        public async Task<Result> HandleAsync(SignOutCommand command, CancellationToken cancellationToken = default)
        {
            if (!currentUser.IsAuthenticated) return Result.Success();

            var (id, name) = (currentUser.UserId, currentUser.UserName);
            session.SignOut();
            await events.TryRecordAsync(SecurityEvent.Create(
                "security.signout", SecurityEventOutcome.Success, id, name, "user", id.ToString()), cancellationToken);
            return Result.Success();
        }
    }

    // ---------------------------------------------------------------- passwords

    /// <summary>
    /// A user changes their OWN password. Re-proves the current password first (the same lockout rules apply), so a session left open
    /// at a till cannot be used to lock the owner out. Works for an account that must change its temporary password.
    /// </summary>
    public sealed record ChangePasswordCommand(string Username, string CurrentPassword, string NewPassword);

    public sealed class ChangePasswordCommandHandler(
        IUserRepository users,
        IUserCredentialRepository credentials,
        IPasswordHasher hasher,
        IUsersUnitOfWork unitOfWork,
        UsersSecurityOptions options,
        ISecurityEventSink? events = null,
        TimeProvider? timeProvider = null)
    {
        private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

        public async Task<Result> HandleAsync(ChangePasswordCommand command, CancellationToken cancellationToken = default)
        {
            var verifier = new CredentialVerifier(users, credentials, hasher, unitOfWork, options, events, _time);
            var verified = await verifier.VerifyAsync(command.Username, command.CurrentPassword, "password change", cancellationToken);
            if (verified.IsFailure) return Result.Failure(verified.Error);

            var (user, credential, _) = verified.Value;
            if (user.Status != Domain.Enums.UserStatus.Active)
                return Result.Failure(UsersSecurityErrors.AccountDisabled());

            var policy = options.Passwords.Validate(command.NewPassword, user.Username);
            if (policy.IsFailure) return policy;

            if (string.Equals(command.NewPassword, command.CurrentPassword, StringComparison.Ordinal))
                return Result.Failure(Error.Validation("Users.Password.Unchanged", "The new password must be different from the current one."));

            var set = credential.SetPassword(hasher.Hash(command.NewPassword), _time.GetUtcNow().UtcDateTime, mustChangePassword: false);
            if (set.IsFailure) return set;

            await unitOfWork.SaveChangesAsync(cancellationToken);
            await events.TryRecordAsync(SecurityEvent.Create(
                "security.password.changed", SecurityEventOutcome.Success, user.Id.Value, user.Username, "user", user.Id.ToString()), cancellationToken);
            return Result.Success();
        }
    }

    /// <summary>
    /// An administrator sets (or resets) a user's password. By default the password is temporary: the user must replace it before the
    /// first sign-in, so the administrator never knows a lasting password. Also unlocks the account.
    /// </summary>
    public sealed record SetUserPasswordCommand(Guid UserId, string NewPassword, bool MustChangeOnNextSignIn = true);

    public sealed class SetUserPasswordCommandHandler(
        IUserRepository users,
        IUserCredentialRepository credentials,
        IPasswordHasher hasher,
        IUsersUnitOfWork unitOfWork,
        IAuthorizationService authorization,
        ICurrentUser currentUser,
        UsersSecurityOptions options,
        ISecurityEventSink? events = null,
        TimeProvider? timeProvider = null)
    {
        private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

        public async Task<Result> HandleAsync(SetUserPasswordCommand command, CancellationToken cancellationToken = default)
        {
            var allowed = await authorization.AuthorizeAsync(UsersCapabilities.Manage, cancellationToken);
            if (allowed.IsFailure) return allowed;

            var user = await users.GetByIdAsync(new UserId(command.UserId), cancellationToken);
            if (user is null)
                return Result.Failure(Error.NotFound("Users.User.NotFound", $"User '{command.UserId}' was not found."));

            var policy = options.Passwords.Validate(command.NewPassword, user.Username);
            if (policy.IsFailure) return policy;

            var now = _time.GetUtcNow().UtcDateTime;
            var hash = hasher.Hash(command.NewPassword);
            var existing = await credentials.GetByUserIdAsync(user.Id, cancellationToken);
            if (existing is null)
            {
                var created = UserCredential.Create(user.Id, hash, now, command.MustChangeOnNextSignIn);
                if (created.IsFailure) return Result.Failure(created.Error);
                await credentials.AddAsync(created.Value, cancellationToken);
            }
            else
            {
                var set = existing.SetPassword(hash, now, command.MustChangeOnNextSignIn);
                if (set.IsFailure) return set;
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
            await events.TryRecordAsync(SecurityEvent.Create(
                "security.password.reset", SecurityEventOutcome.Success, currentUser.UserId, currentUser.UserName,
                "user", user.Id.ToString(), command.MustChangeOnNextSignIn ? "temporary password set" : "password set"), cancellationToken);
            return Result.Success();
        }
    }

    // ---------------------------------------------------------------- first-run administrator

    /// <summary>The name of the built-in role created for the first administrator.</summary>
    public static class AdministratorRole
    {
        public const string Name = "Administrator";
    }

    /// <summary>
    /// First-run setup: creates the first user, an "Administrator" role holding every capability the installed modules declare, and the
    /// user's password. It is the ONE operation that needs no signed-in user, and it only works while NO user exists at all; afterwards
    /// it is permanently refused. It does not sign anyone in.
    /// </summary>
    public sealed record BootstrapAdministratorCommand(string Username, string DisplayName, string Password);

    public sealed class BootstrapAdministratorCommandHandler(
        IUserRepository users,
        IRoleRepository roles,
        IUserCredentialRepository credentials,
        IPasswordHasher hasher,
        IUsersUnitOfWork unitOfWork,
        ICapabilityCatalog catalog,
        UsersSecurityOptions options,
        ISecurityEventSink? events = null,
        TimeProvider? timeProvider = null)
    {
        private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

        public async Task<Result<Guid>> HandleAsync(BootstrapAdministratorCommand command, CancellationToken cancellationToken = default)
        {
            if (await users.AnyAsync(cancellationToken))
            {
                await events.TryRecordAsync(SecurityEvent.Create(
                    "security.bootstrap.refused", SecurityEventOutcome.Denied, subjectType: "user",
                    summary: "first-run setup was attempted but users already exist"), cancellationToken);
                return Result.Failure<Guid>(Error.Conflict("Users.Bootstrap.AlreadyInitialized", "The system already has users; first-run setup is no longer available."));
            }

            var created = User.Create(command.Username, command.DisplayName);
            if (created.IsFailure) return Result.Failure<Guid>(created.Error);
            var user = created.Value;

            var policy = options.Passwords.Validate(command.Password, user.Username);
            if (policy.IsFailure) return Result.Failure<Guid>(policy.Error);

            var role = Role.Create(AdministratorRole.Name, "Full access to everything the installed modules offer.");
            if (role.IsFailure) return Result.Failure<Guid>(role.Error);
            foreach (var capability in catalog.All)
            {
                var granted = role.Value.Grant(capability.Code);
                if (granted.IsFailure) return Result.Failure<Guid>(granted.Error);
            }

            var assigned = user.AssignRole(role.Value.Id);
            if (assigned.IsFailure) return Result.Failure<Guid>(assigned.Error);

            var credential = UserCredential.Create(user.Id, hasher.Hash(command.Password), _time.GetUtcNow().UtcDateTime);
            if (credential.IsFailure) return Result.Failure<Guid>(credential.Error);

            await users.AddAsync(user, cancellationToken);
            await roles.AddAsync(role.Value, cancellationToken);
            await credentials.AddAsync(credential.Value, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);

            await events.TryRecordAsync(SecurityEvent.Create(
                "security.bootstrap.administrator-created", SecurityEventOutcome.Success, user.Id.Value, user.Username,
                "user", user.Id.ToString(), $"first administrator created with {catalog.All.Count} capabilities"), cancellationToken);
            return Result.Success(user.Id.Value);
        }
    }
}
