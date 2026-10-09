using System.Text.RegularExpressions;
using Platform.Core.Results;

namespace Users.Domain.ValueObjects
{
    public readonly record struct UserId(Guid Value)
    {
        public static UserId New() => new(Guid.NewGuid());
        public static UserId Empty => new(Guid.Empty);
        public override string ToString() => Value.ToString();
    }

    public readonly record struct RoleId(Guid Value)
    {
        public static RoleId New() => new(Guid.NewGuid());
        public static RoleId Empty => new(Guid.Empty);
        public override string ToString() => Value.ToString();
    }

    /// <summary>
    /// A permission code such as "sales.refund" or "inventory.adjust": lower-case dot-separated segments (at least two).
    /// Users only STORES permission codes; what a code means is decided by the module that checks it.
    /// </summary>
    public readonly partial record struct PermissionCode(string Value)
    {
        public const int MaxLength = 100;

        [GeneratedRegex("^[a-z][a-z0-9]*(\\.[a-z][a-z0-9]*)+$")]
        private static partial Regex Pattern();

        public static Result<PermissionCode> Create(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return Result.Failure<PermissionCode>(Error.Validation("Users.Permission.Required", "A permission code is required."));

            var normalized = value.Trim().ToLowerInvariant();
            if (normalized.Length > MaxLength)
                return Result.Failure<PermissionCode>(Error.Validation("Users.Permission.TooLong", $"A permission code cannot exceed {MaxLength} characters."));
            if (!Pattern().IsMatch(normalized))
                return Result.Failure<PermissionCode>(Error.Validation(
                    "Users.Permission.InvalidFormat", "A permission code is lower-case dot-separated words, e.g. \"sales.refund\"."));

            return Result.Success(new PermissionCode(normalized));
        }

        public override string ToString() => Value;
    }
}

namespace Users.Domain.Enums
{
    public enum UserStatus
    {
        Active = 1,

        /// <summary>The user can no longer be selected or granted anything. The record is kept for history.</summary>
        Inactive = 2
    }
}

namespace Users.Domain.Entities
{
    using Users.Domain.Enums;
    using Users.Domain.ValueObjects;

    /// <summary>
    /// A role: a named set of permission codes (aggregate root). Roles are data - Users does not know what the codes do.
    /// </summary>
    public sealed class Role
    {
        public const int MaxNameLength = 50;
        public const int MaxDescriptionLength = 200;

        private readonly List<RolePermission> _permissions = [];

        private Role() { }

        public RoleId Id { get; private set; }
        public string Name { get; private set; } = string.Empty;
        public string? Description { get; private set; }
        public DateTime CreatedAt { get; private set; }

        public IReadOnlyCollection<RolePermission> Permissions => _permissions;

        public static Result<Role> Create(string name, string? description = null)
        {
            var validName = ValidateName(name);
            if (validName.IsFailure) return Result.Failure<Role>(validName.Error);
            var validDescription = ValidateDescription(description);
            if (validDescription.IsFailure) return Result.Failure<Role>(validDescription.Error);

            return Result.Success(new Role
            {
                Id = RoleId.New(),
                Name = validName.Value,
                Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                CreatedAt = DateTime.UtcNow
            });
        }

        public Result UpdateDescription(string? description)
        {
            var valid = ValidateDescription(description);
            if (valid.IsFailure) return valid;

            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
            return Result.Success();
        }

        public Result Grant(string permission)
        {
            var code = PermissionCode.Create(permission);
            if (code.IsFailure) return Result.Failure(code.Error);
            if (_permissions.Any(p => p.Permission == code.Value.Value))
                return Result.Failure(Error.Conflict("Users.Role.PermissionAlreadyGranted", $"The role already has the permission '{code.Value}'."));

            _permissions.Add(new RolePermission(Id, code.Value.Value));
            return Result.Success();
        }

        public Result Revoke(string permission)
        {
            var code = PermissionCode.Create(permission);
            if (code.IsFailure) return Result.Failure(code.Error);

            var existing = _permissions.FirstOrDefault(p => p.Permission == code.Value.Value);
            if (existing is null)
                return Result.Failure(Error.NotFound("Users.Role.PermissionNotGranted", $"The role does not have the permission '{code.Value}'."));

            _permissions.Remove(existing);
            return Result.Success();
        }

        private static Result<string> ValidateName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return Result.Failure<string>(Error.Validation("Users.Role.NameRequired", "A role name is required."));
            if (name.Trim().Length > MaxNameLength)
                return Result.Failure<string>(Error.Validation("Users.Role.NameTooLong", $"The role name cannot exceed {MaxNameLength} characters."));
            return Result.Success(name.Trim());
        }

        private static Result ValidateDescription(string? description)
            => description is not null && description.Trim().Length > MaxDescriptionLength
                ? Result.Failure(Error.Validation("Users.Role.DescriptionTooLong", $"The description cannot exceed {MaxDescriptionLength} characters."))
                : Result.Success();
    }

    /// <summary>A permission code granted to a role (child of <see cref="Role"/>).</summary>
    public sealed class RolePermission
    {
        private RolePermission() { }

        internal RolePermission(RoleId roleId, string permission)
        {
            RoleId = roleId;
            Permission = permission;
        }

        public RoleId RoleId { get; private set; }
        public string Permission { get; private set; } = string.Empty;
    }

    /// <summary>
    /// A person who operates the system (aggregate root). This is NOT authentication: a user has no password or credential, only an
    /// identity, a status and role assignments. Sign-in/security belongs to a later stage; other modules identify "who" through the contracts.
    /// </summary>
    public sealed class User
    {
        public const int MaxUsernameLength = 50;
        public const int MaxDisplayNameLength = 100;
        public const int MaxEmailLength = 200;
        public const int MaxLanguageLength = 10;

        private readonly List<UserRole> _roles = [];

        private User() { }

        public UserId Id { get; private set; }

        /// <summary>Unique, stored lower-case (letters, digits, '.', '_' and '-').</summary>
        public string Username { get; private set; } = string.Empty;

        public string DisplayName { get; private set; } = string.Empty;
        public string? Email { get; private set; }

        /// <summary>
        /// The screen language this user works in (FIX-13b), a culture name such as "ar" or "en"; null = the installation's default. Applied by
        /// the desktop when the user signs in.
        /// </summary>
        public string? Language { get; private set; }

        public UserStatus Status { get; private set; }
        public DateTime CreatedAt { get; private set; }
        public DateTime? UpdatedAt { get; private set; }

        public IReadOnlyCollection<UserRole> Roles => _roles;

        public static Result<User> Create(string username, string displayName, string? email = null)
        {
            var name = NormalizeUsername(username);
            if (name.IsFailure) return Result.Failure<User>(name.Error);
            var details = Validate(displayName, email);
            if (details.IsFailure) return Result.Failure<User>(details.Error);

            return Result.Success(new User
            {
                Id = UserId.New(),
                Username = name.Value,
                DisplayName = displayName.Trim(),
                Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim(),
                Status = UserStatus.Active,
                CreatedAt = DateTime.UtcNow
            });
        }

        public static Result<string> NormalizeUsername(string? username)
        {
            if (string.IsNullOrWhiteSpace(username))
                return Result.Failure<string>(Error.Validation("Users.User.UsernameRequired", "A username is required."));

            var value = username.Trim().ToLowerInvariant();
            if (value.Length > MaxUsernameLength)
                return Result.Failure<string>(Error.Validation("Users.User.UsernameTooLong", $"The username cannot exceed {MaxUsernameLength} characters."));
            if (!value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
                return Result.Failure<string>(Error.Validation(
                    "Users.User.UsernameInvalid", "A username may only contain letters, digits, '.', '_' and '-'."));

            return Result.Success(value);
        }

        public Result Update(string displayName, string? email)
        {
            var valid = Validate(displayName, email);
            if (valid.IsFailure) return valid;

            DisplayName = displayName.Trim();
            Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
            UpdatedAt = DateTime.UtcNow;
            return Result.Success();
        }

        /// <summary>Chooses the user's screen language (a culture name like "ar", "en" or "ar-EG"); null or empty = the installation default.</summary>
        public Result SetLanguage(string? language)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                Language = null;
            }
            else
            {
                var value = language.Trim();
                if (value.Length > MaxLanguageLength || !value.All(c => char.IsAsciiLetter(c) || c == '-') || value.StartsWith('-') || value.EndsWith('-'))
                    return Result.Failure(Error.Validation("Users.User.LanguageInvalid", "The language must be a language code such as \"en\" or \"ar\"."));
                Language = value.ToLowerInvariant();
            }

            UpdatedAt = DateTime.UtcNow;
            return Result.Success();
        }

        public Result Deactivate()
        {
            if (Status == UserStatus.Inactive)
                return Result.Failure(Error.Conflict("Users.User.AlreadyInactive", "The user is already inactive."));

            Status = UserStatus.Inactive;
            UpdatedAt = DateTime.UtcNow;
            return Result.Success();
        }

        public Result Reactivate()
        {
            if (Status == UserStatus.Active)
                return Result.Failure(Error.Conflict("Users.User.AlreadyActive", "The user is already active."));

            Status = UserStatus.Active;
            UpdatedAt = DateTime.UtcNow;
            return Result.Success();
        }

        public Result AssignRole(RoleId roleId)
        {
            if (roleId == RoleId.Empty)
                return Result.Failure(Error.Validation("Users.User.RoleRequired", "A role is required."));
            if (Status != UserStatus.Active)
                return Result.Failure(Error.Conflict("Users.User.Inactive", "Roles cannot be assigned to an inactive user."));
            if (_roles.Any(r => r.RoleId == roleId))
                return Result.Failure(Error.Conflict("Users.User.RoleAlreadyAssigned", "The user already has this role."));

            _roles.Add(new UserRole(Id, roleId));
            UpdatedAt = DateTime.UtcNow;
            return Result.Success();
        }

        public Result RemoveRole(RoleId roleId)
        {
            var existing = _roles.FirstOrDefault(r => r.RoleId == roleId);
            if (existing is null)
                return Result.Failure(Error.NotFound("Users.User.RoleNotAssigned", "The user does not have this role."));

            _roles.Remove(existing);
            UpdatedAt = DateTime.UtcNow;
            return Result.Success();
        }

        private static Result Validate(string? displayName, string? email)
        {
            if (string.IsNullOrWhiteSpace(displayName))
                return Result.Failure(Error.Validation("Users.User.DisplayNameRequired", "A display name is required."));
            if (displayName.Trim().Length > MaxDisplayNameLength)
                return Result.Failure(Error.Validation("Users.User.DisplayNameTooLong", $"The display name cannot exceed {MaxDisplayNameLength} characters."));

            if (!string.IsNullOrWhiteSpace(email))
            {
                var e = email.Trim();
                if (e.Length > MaxEmailLength)
                    return Result.Failure(Error.Validation("Users.User.EmailTooLong", $"The email cannot exceed {MaxEmailLength} characters."));
                var at = e.IndexOf('@');
                if (at <= 0 || at != e.LastIndexOf('@') || at == e.Length - 1 || e.Contains(' '))
                    return Result.Failure(Error.Validation("Users.User.EmailInvalid", "The email address is not valid."));
            }

            return Result.Success();
        }
    }

    /// <summary>A role assigned to a user (child of <see cref="User"/>; the role is in the same module).</summary>
    public sealed class UserRole
    {
        private UserRole() { }

        internal UserRole(UserId userId, RoleId roleId)
        {
            UserId = userId;
            RoleId = roleId;
        }

        public UserId UserId { get; private set; }
        public RoleId RoleId { get; private set; }
    }
}
