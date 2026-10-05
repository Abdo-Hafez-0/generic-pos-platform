namespace Users.Contracts.Models
{
    /// <summary>Minimal identity of a user for other modules. Never exposes Users.Domain types.</summary>
    public sealed record UserLookupResult(Guid UserId, string Username, string DisplayName, bool IsActive);
}

namespace Users.Contracts.Interfaces
{
    using Users.Contracts.Models;

    /// <summary>
    /// Lets other modules identify "who" (cashier, approver, auditor...) without knowing how users are stored.
    /// Implemented by Users.Infrastructure.Services.UserLookup. This is NOT authentication.
    /// </summary>
    public interface IUserLookup
    {
        Task<UserLookupResult?> FindByIdAsync(Guid userId, CancellationToken cancellationToken = default);

        /// <summary>Case-insensitive.</summary>
        Task<UserLookupResult?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Answers "does this user hold this permission code?" for modules that want to guard an action.
    /// Inactive or unknown users hold nothing. Permission codes are owned by the module that checks them; Users only stores them.
    /// </summary>
    public interface IUserPermissionChecker
    {
        Task<bool> HasPermissionAsync(Guid userId, string permission, CancellationToken cancellationToken = default);

        /// <summary>All distinct permission codes the (active) user holds through their roles, sorted.</summary>
        Task<IReadOnlyList<string>> GetPermissionsAsync(Guid userId, CancellationToken cancellationToken = default);
    }
}
