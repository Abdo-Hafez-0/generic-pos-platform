using Platform.Core.Results;

namespace Users.Domain.ValueObjects
{
    /// <summary>
    /// The rules a new password must satisfy. Length and "not trivially guessable" matter more than character-class rules, so this checks
    /// length, a handful of obviously weak patterns and a short list of the most common passwords. Pure: no I/O, no configuration reading.
    /// </summary>
    public sealed record PasswordPolicy(int MinimumLength = 10, int MaximumLength = 128)
    {
        public const int MinimumLengthFloor = 8;
        public const int AbsoluteMaximumLength = 128;
        public const int MinimumDistinctCharacters = 5;

        public static PasswordPolicy Default { get; } = new();

        // The very common passwords (lower-case), checked after stripping non-alphanumerics from the candidate.
        private static readonly HashSet<string> Common = new(StringComparer.Ordinal)
        {
            "password", "password1", "password12", "password123", "passw0rd", "p@ssw0rd", "letmein", "welcome", "welcome1", "welcome123",
            "qwerty", "qwerty123", "qwertyuiop", "qwerty1234", "123456", "1234567", "12345678", "123456789", "1234567890", "0123456789",
            "abc123", "abcd1234", "iloveyou", "admin", "admin123", "administrator", "root", "changeme", "default", "master", "monkey",
            "dragon", "football", "baseball", "sunshine", "princess", "trustno1", "shadow", "superman", "batman", "login", "access",
            "cashier", "cashier123", "manager", "manager123", "genericpos", "pos12345", "store123", "retail123"
        };

        /// <summary>The effective policy: the configured values, never weaker than the built-in floor.</summary>
        public PasswordPolicy Normalized()
            => new(Math.Max(MinimumLength, MinimumLengthFloor), Math.Clamp(MaximumLength, Math.Max(MinimumLength, MinimumLengthFloor), AbsoluteMaximumLength));

        public Result Validate(string? password, string? username)
        {
            if (string.IsNullOrWhiteSpace(password))
                return Error.Validation("Users.Password.Required", "A password is required.");

            var policy = Normalized();
            if (password.Length < policy.MinimumLength)
                return Error.Validation("Users.Password.TooShort", $"The password must be at least {policy.MinimumLength} characters long.");

            if (password.Length > policy.MaximumLength)
                return Error.Validation("Users.Password.TooLong", $"The password cannot exceed {policy.MaximumLength} characters.");

            if (password.Distinct().Count() < MinimumDistinctCharacters)
                return Error.Validation("Users.Password.TooSimple", "The password is too repetitive. Use a longer, less predictable password.");

            if (!string.IsNullOrWhiteSpace(username) && username.Trim().Length >= 3
                && password.Contains(username.Trim(), StringComparison.OrdinalIgnoreCase))
                return Error.Validation("Users.Password.ContainsUsername", "The password cannot contain the username.");

            var squashed = new string(password.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
            if (Common.Contains(squashed))
                return Error.Validation("Users.Password.TooCommon", "That password is too common. Choose a different one.");

            return Result.Success();
        }
    }

    /// <summary>How many wrong passwords an account tolerates before it is locked, and for how long.</summary>
    public sealed record LockoutPolicy(int MaxFailedAttempts = 5, TimeSpan? LockoutDuration = null)
    {
        public const int MaxFailedAttemptsFloor = 3;
        public static readonly TimeSpan DefaultDuration = TimeSpan.FromMinutes(15);

        public static LockoutPolicy Default { get; } = new();

        public TimeSpan Duration => LockoutDuration is { } d && d > TimeSpan.Zero ? d : DefaultDuration;

        /// <summary>The effective policy: never more lenient than the built-in floor, always with a positive duration.</summary>
        public LockoutPolicy Normalized() => new(Math.Max(MaxFailedAttempts, MaxFailedAttemptsFloor), Duration);
    }
}

namespace Users.Domain.Entities
{
    using Users.Domain.ValueObjects;

    /// <summary>
    /// How a user proves who they are: an opaque password HASH (never the password), when it was set, and the sign-in failure state used
    /// for lockout. Kept apart from <see cref="User"/> so that loading a user for display never loads credential material.
    /// The hash format belongs to the hashing service (Users.Application/Infrastructure); the domain only stores and compares nothing.
    /// </summary>
    public sealed class UserCredential
    {
        public const int MaxHashLength = 512;

        private UserCredential() { }

        public UserId UserId { get; private set; }

        /// <summary>Self-describing hash string (algorithm, cost, salt and hash). Never reversible to the password.</summary>
        public string PasswordHash { get; private set; } = string.Empty;

        public DateTime PasswordChangedAt { get; private set; }

        /// <summary>True after an administrator set a temporary password: the user must choose their own before signing in.</summary>
        public bool MustChangePassword { get; private set; }

        public int FailedAttempts { get; private set; }

        public DateTime? LockedUntil { get; private set; }

        public DateTime? LastFailedAt { get; private set; }

        public DateTime? LastSignInAt { get; private set; }

        public static Result<UserCredential> Create(UserId userId, string passwordHash, DateTime nowUtc, bool mustChangePassword = false)
        {
            if (userId == UserId.Empty)
                return Result.Failure<UserCredential>(Error.Validation("Users.Credential.UserRequired", "A user is required."));

            var valid = ValidateHash(passwordHash);
            if (valid.IsFailure) return Result.Failure<UserCredential>(valid.Error);

            return Result.Success(new UserCredential
            {
                UserId = userId,
                PasswordHash = passwordHash,
                PasswordChangedAt = nowUtc,
                MustChangePassword = mustChangePassword
            });
        }

        /// <summary>Replaces the password. Clears every failure and any lock: the account starts afresh.</summary>
        public Result SetPassword(string passwordHash, DateTime nowUtc, bool mustChangePassword)
        {
            var valid = ValidateHash(passwordHash);
            if (valid.IsFailure) return valid;

            PasswordHash = passwordHash;
            PasswordChangedAt = nowUtc;
            MustChangePassword = mustChangePassword;
            FailedAttempts = 0;
            LockedUntil = null;
            return Result.Success();
        }

        /// <summary>Stores a stronger hash of the SAME password (cost upgrade after a successful sign-in). Nothing else changes.</summary>
        public Result UpgradeHash(string passwordHash)
        {
            var valid = ValidateHash(passwordHash);
            if (valid.IsFailure) return valid;

            PasswordHash = passwordHash;
            return Result.Success();
        }

        public bool IsLocked(DateTime nowUtc) => LockedUntil is { } until && nowUtc < until;

        /// <summary>
        /// Counts a wrong password. Returns true when THIS failure locked the account. An expired lock starts a fresh count first.
        /// Calling it while the account is locked changes nothing (a locked account is never even tried).
        /// </summary>
        public bool RegisterFailure(DateTime nowUtc, LockoutPolicy policy)
        {
            if (IsLocked(nowUtc)) return false;

            if (LockedUntil is not null)
            {
                LockedUntil = null;
                FailedAttempts = 0;
            }

            var effective = policy.Normalized();
            FailedAttempts++;
            LastFailedAt = nowUtc;
            if (FailedAttempts < effective.MaxFailedAttempts) return false;

            LockedUntil = nowUtc + effective.Duration;
            return true;
        }

        public void RegisterSuccess(DateTime nowUtc)
        {
            FailedAttempts = 0;
            LockedUntil = null;
            LastSignInAt = nowUtc;
        }

        /// <summary>Administrative unlock.</summary>
        public void Unlock()
        {
            FailedAttempts = 0;
            LockedUntil = null;
        }

        private static Result ValidateHash(string? hash)
            => string.IsNullOrWhiteSpace(hash) || hash.Length > MaxHashLength
                ? Error.Validation("Users.Credential.HashInvalid", "A password hash is required.")
                : Result.Success();
    }
}
