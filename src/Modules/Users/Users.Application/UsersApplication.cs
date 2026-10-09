using Platform.Application.Abstractions.Authorization;
using Platform.Application.Abstractions.Security;
using Platform.Core.Results;
using Users.Domain.Entities;
using Users.Domain.ValueObjects;

namespace Users.Application.Abstractions
{
    /// <summary>Saves changes to the Users module's own persistence (UsersDbContext).</summary>
    public interface IUsersUnitOfWork
    {
        Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    }
}

namespace Users.Application.Repositories
{
    public interface IUserRepository
    {
        Task<User?> GetByIdAsync(UserId id, CancellationToken cancellationToken = default);
        Task<User?> GetByUsernameAsync(string normalizedUsername, CancellationToken cancellationToken = default);
        Task AddAsync(User user, CancellationToken cancellationToken = default);
        Task<bool> AnyAsync(CancellationToken cancellationToken = default);
        Task<(IReadOnlyList<User> Items, int TotalCount)> ListAsync(string? search, bool includeInactive, int skip, int take, CancellationToken cancellationToken = default);

        /// <summary>Every role assignment of every ACTIVE user, as stored (FIX-01e: who can still manage users).</summary>
        Task<IReadOnlyList<(Guid UserId, Guid RoleId)>> GetActiveUserRolesAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>Credential material (password hash, lockout state). Separate from <see cref="IUserRepository"/> so that ordinary user queries never touch it.</summary>
    public interface IUserCredentialRepository
    {
        Task<UserCredential?> GetByUserIdAsync(UserId userId, CancellationToken cancellationToken = default);
        Task AddAsync(UserCredential credential, CancellationToken cancellationToken = default);
    }

    public interface IRoleRepository
    {
        Task<Role?> GetByIdAsync(RoleId id, CancellationToken cancellationToken = default);
        Task<Role?> GetByNameAsync(string name, CancellationToken cancellationToken = default);
        Task AddAsync(Role role, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<Role>> ListAsync(CancellationToken cancellationToken = default);
        Task<IReadOnlyList<Role>> GetByIdsAsync(IReadOnlyCollection<RoleId> ids, CancellationToken cancellationToken = default);
    }
}

namespace Users.Application.DTOs
{
    public sealed record RoleDto(Guid RoleId, string Name, string? Description, IReadOnlyList<string> Permissions);

    public sealed record UserDto(
        Guid UserId, string Username, string DisplayName, string? Email, Domain.Enums.UserStatus Status,
        IReadOnlyList<RoleSummaryDto> Roles, DateTime CreatedAt, string? Language = null);

    public sealed record RoleSummaryDto(Guid RoleId, string Name);

    public sealed record UserSummaryDto(Guid UserId, string Username, string DisplayName, Domain.Enums.UserStatus Status);

    public sealed record PagedUsers(IReadOnlyList<UserSummaryDto> Items, int TotalCount, int Page, int PageSize);
}

namespace Users.Application.Commands
{
    using Users.Application.Abstractions;
    using Users.Application.Repositories;

    // ------------------------------------------------------------------ users

    public sealed record CreateUserCommand(string Username, string DisplayName, string? Email = null);

    public sealed class CreateUserCommandHandler(IUserRepository users, IUsersUnitOfWork unitOfWork, IAuthorizationService authorization, ICurrentUser? currentUser = null, ISecurityEventSink? events = null)
    {
        public async Task<Result<Guid>> HandleAsync(CreateUserCommand command, CancellationToken cancellationToken = default)
        {
            var allowed = await authorization.AuthorizeAsync(Users.Application.Security.UsersCapabilities.Manage, cancellationToken);
            if (allowed.IsFailure) return Result.Failure<Guid>(allowed.Error);

            var created = User.Create(command.Username, command.DisplayName, command.Email);
            if (created.IsFailure) return Result.Failure<Guid>(created.Error);

            if (await users.GetByUsernameAsync(created.Value.Username, cancellationToken) is not null)
                return Result.Failure<Guid>(Error.Conflict("Users.CreateUser.DuplicateUsername", $"The username '{created.Value.Username}' is already taken."));

            await users.AddAsync(created.Value, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await events.TryRecordAsync(SecurityEvent.Create(
                "security.user.created", SecurityEventOutcome.Success, currentUser?.UserId, currentUser?.UserName,
                "user", created.Value.Id.ToString(), "user created"), cancellationToken);
            return Result.Success(created.Value.Id.Value);
        }
    }

    public sealed record UpdateUserCommand(Guid UserId, string DisplayName, string? Email);

    public sealed class UpdateUserCommandHandler(IUserRepository users, IUsersUnitOfWork unitOfWork, IAuthorizationService authorization)
    {
        public async Task<Result> HandleAsync(UpdateUserCommand command, CancellationToken cancellationToken = default)
        {
            var allowed = await authorization.AuthorizeAsync(Users.Application.Security.UsersCapabilities.Manage, cancellationToken);
            if (allowed.IsFailure) return allowed;

            var user = await users.GetByIdAsync(new UserId(command.UserId), cancellationToken);
            if (user is null) return Result.Failure(UsersErrors.UserNotFound(command.UserId));

            var result = user.Update(command.DisplayName, command.Email);
            if (result.IsFailure) return result;

            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }
    }

    /// <summary>
    /// FIX-13b: sets a user's screen language (null = the installation default). Everyone may choose their OWN language; choosing another
    /// user's needs users.manage.
    /// </summary>
    public sealed record SetUserLanguageCommand(Guid UserId, string? Language);

    public sealed class SetUserLanguageCommandHandler(IUserRepository users, IUsersUnitOfWork unitOfWork, IAuthorizationService authorization, ICurrentUser? currentUser = null)
    {
        public async Task<Result> HandleAsync(SetUserLanguageCommand command, CancellationToken cancellationToken = default)
        {
            var self = currentUser is { IsAuthenticated: true } me && me.UserId == command.UserId;
            if (!self)
            {
                var allowed = await authorization.AuthorizeAsync(Users.Application.Security.UsersCapabilities.Manage, cancellationToken);
                if (allowed.IsFailure) return allowed;
            }

            var user = await users.GetByIdAsync(new UserId(command.UserId), cancellationToken);
            if (user is null) return Result.Failure(UsersErrors.UserNotFound(command.UserId));

            var result = user.SetLanguage(command.Language);
            if (result.IsFailure) return result;

            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }
    }

    public sealed record DeactivateUserCommand(Guid UserId);

    public sealed class DeactivateUserCommandHandler(IUserRepository users, IRoleRepository roles, IUsersUnitOfWork unitOfWork, IAuthorizationService authorization, ICurrentUser? currentUser = null, ISecurityEventSink? events = null)
    {
        public async Task<Result> HandleAsync(DeactivateUserCommand command, CancellationToken cancellationToken = default)
        {
            var allowed = await authorization.AuthorizeAsync(Users.Application.Security.UsersCapabilities.Manage, cancellationToken);
            if (allowed.IsFailure) return allowed;

            if (currentUser is { IsAuthenticated: true } && currentUser.UserId == command.UserId)
                return Result.Failure(AdministrationGuard.SelfDeactivation());

            var user = await users.GetByIdAsync(new UserId(command.UserId), cancellationToken);
            if (user is null) return Result.Failure(UsersErrors.UserNotFound(command.UserId));

            if (user.Status == Domain.Enums.UserStatus.Active
                && !await AdministrationGuard.SomeoneCanStillManageAsync(users, roles, cancellationToken, deactivatedUser: command.UserId))
                return Result.Failure(AdministrationGuard.LastAdministrator());

            var result = user.Deactivate();
            if (result.IsFailure) return result;

            await unitOfWork.SaveChangesAsync(cancellationToken);
            await events.TryRecordAsync(SecurityEvent.Create(
                "security.user.deactivated", SecurityEventOutcome.Success, currentUser?.UserId, currentUser?.UserName,
                "user", command.UserId.ToString(), "user deactivated: sign-in and every permission end immediately"), cancellationToken);
            return Result.Success();
        }
    }

    public sealed record ReactivateUserCommand(Guid UserId);

    public sealed class ReactivateUserCommandHandler(IUserRepository users, IUsersUnitOfWork unitOfWork, IAuthorizationService authorization, ICurrentUser? currentUser = null, ISecurityEventSink? events = null)
    {
        public async Task<Result> HandleAsync(ReactivateUserCommand command, CancellationToken cancellationToken = default)
        {
            var allowed = await authorization.AuthorizeAsync(Users.Application.Security.UsersCapabilities.Manage, cancellationToken);
            if (allowed.IsFailure) return allowed;

            var user = await users.GetByIdAsync(new UserId(command.UserId), cancellationToken);
            if (user is null) return Result.Failure(UsersErrors.UserNotFound(command.UserId));

            var result = user.Reactivate();
            if (result.IsFailure) return result;

            await unitOfWork.SaveChangesAsync(cancellationToken);
            await events.TryRecordAsync(SecurityEvent.Create(
                "security.user.reactivated", SecurityEventOutcome.Success, currentUser?.UserId, currentUser?.UserName,
                "user", command.UserId.ToString(), "user reactivated"), cancellationToken);
            return Result.Success();
        }
    }

    public sealed record AssignRoleCommand(Guid UserId, Guid RoleId);

    public sealed class AssignRoleCommandHandler(IUserRepository users, IRoleRepository roles, IUsersUnitOfWork unitOfWork, IAuthorizationService authorization, ICurrentUser? currentUser = null, ISecurityEventSink? events = null)
    {
        public async Task<Result> HandleAsync(AssignRoleCommand command, CancellationToken cancellationToken = default)
        {
            var allowed = await authorization.AuthorizeAsync(Users.Application.Security.UsersCapabilities.Manage, cancellationToken);
            if (allowed.IsFailure) return allowed;

            var user = await users.GetByIdAsync(new UserId(command.UserId), cancellationToken);
            if (user is null) return Result.Failure(UsersErrors.UserNotFound(command.UserId));

            if (await roles.GetByIdAsync(new RoleId(command.RoleId), cancellationToken) is null)
                return Result.Failure(Error.NotFound("Users.AssignRole.RoleNotFound", $"Role '{command.RoleId}' was not found."));

            var result = user.AssignRole(new RoleId(command.RoleId));
            if (result.IsFailure) return result;

            await unitOfWork.SaveChangesAsync(cancellationToken);
            await events.TryRecordAsync(SecurityEvent.Create(
                "security.role.assigned", SecurityEventOutcome.Success, currentUser?.UserId, currentUser?.UserName,
                "user", command.UserId.ToString(), $"role {command.RoleId} assigned"), cancellationToken);
            return Result.Success();
        }
    }

    public sealed record RemoveRoleCommand(Guid UserId, Guid RoleId);

    public sealed class RemoveRoleCommandHandler(IUserRepository users, IRoleRepository roles, IUsersUnitOfWork unitOfWork, IAuthorizationService authorization, ICurrentUser? currentUser = null, ISecurityEventSink? events = null)
    {
        public async Task<Result> HandleAsync(RemoveRoleCommand command, CancellationToken cancellationToken = default)
        {
            var allowed = await authorization.AuthorizeAsync(Users.Application.Security.UsersCapabilities.Manage, cancellationToken);
            if (allowed.IsFailure) return allowed;

            var user = await users.GetByIdAsync(new UserId(command.UserId), cancellationToken);
            if (user is null) return Result.Failure(UsersErrors.UserNotFound(command.UserId));

            if (!await AdministrationGuard.SomeoneCanStillManageAsync(users, roles, cancellationToken, removedRole: (command.UserId, command.RoleId)))
                return Result.Failure(AdministrationGuard.LastAdministrator());

            var result = user.RemoveRole(new RoleId(command.RoleId));
            if (result.IsFailure) return result;

            await unitOfWork.SaveChangesAsync(cancellationToken);
            await events.TryRecordAsync(SecurityEvent.Create(
                "security.role.removed", SecurityEventOutcome.Success, currentUser?.UserId, currentUser?.UserName,
                "user", command.UserId.ToString(), $"role {command.RoleId} removed"), cancellationToken);
            return Result.Success();
        }
    }

    // ------------------------------------------------------------------ roles

    public sealed record CreateRoleCommand(string Name, string? Description = null);

    public sealed class CreateRoleCommandHandler(IRoleRepository roles, IUsersUnitOfWork unitOfWork, IAuthorizationService authorization, ICurrentUser? currentUser = null, ISecurityEventSink? events = null)
    {
        public async Task<Result<Guid>> HandleAsync(CreateRoleCommand command, CancellationToken cancellationToken = default)
        {
            var allowed = await authorization.AuthorizeAsync(Users.Application.Security.UsersCapabilities.Manage, cancellationToken);
            if (allowed.IsFailure) return Result.Failure<Guid>(allowed.Error);

            var created = Role.Create(command.Name, command.Description);
            if (created.IsFailure) return Result.Failure<Guid>(created.Error);

            if (await roles.GetByNameAsync(created.Value.Name, cancellationToken) is not null)
                return Result.Failure<Guid>(Error.Conflict("Users.CreateRole.DuplicateName", $"A role named '{created.Value.Name}' already exists."));

            await roles.AddAsync(created.Value, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await events.TryRecordAsync(SecurityEvent.Create(
                "security.role.created", SecurityEventOutcome.Success, currentUser?.UserId, currentUser?.UserName,
                "role", created.Value.Id.ToString(), "role created"), cancellationToken);
            return Result.Success(created.Value.Id.Value);
        }
    }

    public sealed record GrantPermissionCommand(Guid RoleId, string Permission);

    public sealed class GrantPermissionCommandHandler(IRoleRepository roles, IUsersUnitOfWork unitOfWork, IAuthorizationService authorization, ICurrentUser? currentUser = null, ISecurityEventSink? events = null)
    {
        public async Task<Result> HandleAsync(GrantPermissionCommand command, CancellationToken cancellationToken = default)
        {
            var allowed = await authorization.AuthorizeAsync(Users.Application.Security.UsersCapabilities.Manage, cancellationToken);
            if (allowed.IsFailure) return allowed;

            var role = await roles.GetByIdAsync(new RoleId(command.RoleId), cancellationToken);
            if (role is null) return Result.Failure(UsersErrors.RoleNotFound(command.RoleId));

            var result = role.Grant(command.Permission);
            if (result.IsFailure) return result;

            await unitOfWork.SaveChangesAsync(cancellationToken);
            await events.TryRecordAsync(SecurityEvent.Create(
                "security.permission.granted", SecurityEventOutcome.Success, currentUser?.UserId, currentUser?.UserName,
                "role", command.RoleId.ToString(), $"permission {command.Permission} granted"), cancellationToken);
            return Result.Success();
        }
    }

    public sealed record RevokePermissionCommand(Guid RoleId, string Permission);

    public sealed class RevokePermissionCommandHandler(IRoleRepository roles, IUserRepository users, IUsersUnitOfWork unitOfWork, IAuthorizationService authorization, ICurrentUser? currentUser = null, ISecurityEventSink? events = null)
    {
        public async Task<Result> HandleAsync(RevokePermissionCommand command, CancellationToken cancellationToken = default)
        {
            var allowed = await authorization.AuthorizeAsync(Users.Application.Security.UsersCapabilities.Manage, cancellationToken);
            if (allowed.IsFailure) return allowed;

            var role = await roles.GetByIdAsync(new RoleId(command.RoleId), cancellationToken);
            if (role is null) return Result.Failure(UsersErrors.RoleNotFound(command.RoleId));

            var requested = string.Concat(command.Permission).Trim();   // null-safe without a null test on the command's value
            if (string.Equals(requested, Users.Application.Security.UsersCapabilities.Manage, StringComparison.OrdinalIgnoreCase)
                && !await AdministrationGuard.SomeoneCanStillManageAsync(users, roles, cancellationToken, revokedPermission: (command.RoleId, requested)))
                return Result.Failure(AdministrationGuard.LastAdministrator());

            var result = role.Revoke(command.Permission);
            if (result.IsFailure) return result;

            await unitOfWork.SaveChangesAsync(cancellationToken);
            await events.TryRecordAsync(SecurityEvent.Create(
                "security.permission.revoked", SecurityEventOutcome.Success, currentUser?.UserId, currentUser?.UserName,
                "role", command.RoleId.ToString(), $"permission {command.Permission} revoked"), cancellationToken);
            return Result.Success();
        }
    }

    /// <summary>
    /// FIX-01e (user decision): user administration can never lock itself out. A change is refused when, after it, NO active user would
    /// hold users.manage through a role - there is no way back inside the application (first-run setup works only while no user exists).
    /// The rule is evaluated on the stored state with the pending change applied as parameters (before anything is modified), so it does
    /// not depend on how the change would be tracked.
    /// </summary>
    internal static class AdministrationGuard
    {
        public const string LastAdministratorCode = "Users.LastAdministrator";
        public const string SelfDeactivationCode = "Users.SelfDeactivation";

        public static Error LastAdministrator() => Error.Conflict(LastAdministratorCode,
            "This would leave nobody who can manage users. Give another active user a role that can manage users first.");

        public static Error SelfDeactivation() => Error.Conflict(SelfDeactivationCode,
            "You cannot deactivate your own account. Another administrator can do it.");

        /// <summary>
        /// True unless the described change takes the number of active users holding users.manage from one or more to zero. (When nobody
        /// holds it already - a host without authentication or a test host - the change does not make anything worse and is allowed.)
        /// </summary>
        public static async Task<bool> SomeoneCanStillManageAsync(
            IUserRepository users,
            IRoleRepository roles,
            CancellationToken cancellationToken,
            Guid? deactivatedUser = null,
            (Guid UserId, Guid RoleId)? removedRole = null,
            (Guid RoleId, string Permission)? revokedPermission = null)
            => await AnyManagerAsync(users, roles, cancellationToken, deactivatedUser, removedRole, revokedPermission)
               || !await AnyManagerAsync(users, roles, cancellationToken);

        private static async Task<bool> AnyManagerAsync(
            IUserRepository users,
            IRoleRepository roles,
            CancellationToken cancellationToken,
            Guid? deactivatedUser = null,
            (Guid UserId, Guid RoleId)? removedRole = null,
            (Guid RoleId, string Permission)? revokedPermission = null)
        {
            var managingRoles = (await roles.ListAsync(cancellationToken))
                .Where(r => r.Permissions.Any(p =>
                    string.Equals(p.Permission, Users.Application.Security.UsersCapabilities.Manage, StringComparison.OrdinalIgnoreCase)
                    && !(revokedPermission is { } revoked && r.Id.Value == revoked.RoleId
                         && string.Equals(revoked.Permission?.Trim(), p.Permission, StringComparison.OrdinalIgnoreCase))))
                .Select(r => r.Id.Value)
                .ToHashSet();
            if (managingRoles.Count == 0) return false;

            return (await users.GetActiveUserRolesAsync(cancellationToken)).Any(a =>
                a.UserId != deactivatedUser
                && managingRoles.Contains(a.RoleId)
                && !(removedRole is { } removed && removed.UserId == a.UserId && removed.RoleId == a.RoleId));
        }
    }

    internal static class UsersErrors
    {
        public static Error UserNotFound(Guid id) => Error.NotFound("Users.User.NotFound", $"User '{id}' was not found.");

        public static Error RoleNotFound(Guid id) => Error.NotFound("Users.Role.NotFound", $"Role '{id}' was not found.");
    }
}

namespace Users.Application.Queries
{
    using Users.Application.DTOs;
    using Users.Application.Repositories;

    public sealed record GetUserQuery(Guid UserId);

    public sealed class GetUserQueryHandler(IUserRepository users, IRoleRepository roles, IAuthorizationService authorization)
    {
        /// <summary>Success with a null value means "no such user"; a failure means the caller may not view users.</summary>
        public async Task<Result<UserDto?>> HandleAsync(GetUserQuery query, CancellationToken cancellationToken = default)
        {
            var allowed = await authorization.AuthorizeAsync(Users.Application.Security.UsersCapabilities.View, cancellationToken);
            if (allowed.IsFailure) return Result.Failure<UserDto?>(allowed.Error);

            var user = await users.GetByIdAsync(new UserId(query.UserId), cancellationToken);
            if (user is null) return Result.Success<UserDto?>(null);

            var assigned = await roles.GetByIdsAsync(user.Roles.Select(r => r.RoleId).ToList(), cancellationToken);
            return Result.Success<UserDto?>(new UserDto(
                user.Id.Value, user.Username, user.DisplayName, user.Email, user.Status,
                assigned.OrderBy(r => r.Name).Select(r => new RoleSummaryDto(r.Id.Value, r.Name)).ToList(), user.CreatedAt, user.Language));
        }
    }

    /// <summary>FIX-13b: a user's screen language (null = the installation default). Everyone may read their OWN; another user's needs users.view.</summary>
    public sealed record GetUserLanguageQuery(Guid UserId);

    public sealed class GetUserLanguageQueryHandler(IUserRepository users, IAuthorizationService authorization, ICurrentUser? currentUser = null)
    {
        public async Task<Result<string?>> HandleAsync(GetUserLanguageQuery query, CancellationToken cancellationToken = default)
        {
            var self = currentUser is { IsAuthenticated: true } me && me.UserId == query.UserId;
            if (!self)
            {
                var allowed = await authorization.AuthorizeAsync(Users.Application.Security.UsersCapabilities.View, cancellationToken);
                if (allowed.IsFailure) return Result.Failure<string?>(allowed.Error);
            }

            var user = await users.GetByIdAsync(new UserId(query.UserId), cancellationToken);
            return user is null ? Result.Failure<string?>(Users.Application.Commands.UsersErrors.UserNotFound(query.UserId)) : Result.Success(user.Language);
        }
    }

    public sealed record ListUsersQuery(string? Search = null, bool IncludeInactive = false, int Page = 1, int PageSize = 50);

    public sealed class ListUsersQueryHandler(IUserRepository users, IAuthorizationService authorization)
    {
        public const int MaxPageSize = 200;

        public async Task<Result<PagedUsers>> HandleAsync(ListUsersQuery query, CancellationToken cancellationToken = default)
        {
            var allowed = await authorization.AuthorizeAsync(Users.Application.Security.UsersCapabilities.View, cancellationToken);
            if (allowed.IsFailure) return Result.Failure<PagedUsers>(allowed.Error);

            var page = Math.Max(1, query.Page);
            var size = Math.Clamp(query.PageSize, 1, MaxPageSize);

            var (items, total) = await users.ListAsync(query.Search, query.IncludeInactive, (page - 1) * size, size, cancellationToken);
            return Result.Success(new PagedUsers(items.Select(u => new UserSummaryDto(u.Id.Value, u.Username, u.DisplayName, u.Status)).ToList(), total, page, size));
        }
    }

    public sealed record GetRoleQuery(Guid RoleId);

    public sealed class GetRoleQueryHandler(IRoleRepository roles, IAuthorizationService authorization)
    {
        /// <summary>Success with a null value means "no such role"; a failure means the caller may not view roles.</summary>
        public async Task<Result<RoleDto?>> HandleAsync(GetRoleQuery query, CancellationToken cancellationToken = default)
        {
            var allowed = await authorization.AuthorizeAsync(Users.Application.Security.UsersCapabilities.View, cancellationToken);
            if (allowed.IsFailure) return Result.Failure<RoleDto?>(allowed.Error);

            return Result.Success((await roles.GetByIdAsync(new RoleId(query.RoleId), cancellationToken))?.ToDto());
        }
    }

    public sealed record ListRolesQuery;

    public sealed class ListRolesQueryHandler(IRoleRepository roles, IAuthorizationService authorization)
    {
        public async Task<Result<IReadOnlyList<RoleDto>>> HandleAsync(ListRolesQuery query, CancellationToken cancellationToken = default)
        {
            var allowed = await authorization.AuthorizeAsync(Users.Application.Security.UsersCapabilities.View, cancellationToken);
            if (allowed.IsFailure) return Result.Failure<IReadOnlyList<RoleDto>>(allowed.Error);

            return Result.Success<IReadOnlyList<RoleDto>>((await roles.ListAsync(cancellationToken)).OrderBy(r => r.Name).Select(r => r.ToDto()).ToList());
        }
    }

    /// <summary>The permission codes a user holds through their roles (nothing for an unknown or inactive user).</summary>
    public sealed record GetUserPermissionsQuery(Guid UserId);

    public sealed class GetUserPermissionsQueryHandler(IUserRepository users, IRoleRepository roles)
    {
        public async Task<IReadOnlyList<string>> HandleAsync(GetUserPermissionsQuery query, CancellationToken cancellationToken = default)
        {
            var user = await users.GetByIdAsync(new UserId(query.UserId), cancellationToken);
            if (user is null || user.Status != Domain.Enums.UserStatus.Active) return [];

            var assigned = await roles.GetByIdsAsync(user.Roles.Select(r => r.RoleId).ToList(), cancellationToken);
            return assigned.SelectMany(r => r.Permissions.Select(p => p.Permission)).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();
        }
    }

    internal static class UsersMapping
    {
        public static RoleDto ToDto(this Role r) => new(
            r.Id.Value, r.Name, r.Description, r.Permissions.Select(p => p.Permission).OrderBy(p => p, StringComparer.Ordinal).ToList());
    }
}
