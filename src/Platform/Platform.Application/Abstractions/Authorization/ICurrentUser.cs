using Platform.Core.Modules;

namespace Platform.Application.Abstractions.Authorization;

/// <summary>
/// Provides access to the currently authenticated user's identity.
/// The UI or host layer sets the current user context; application handlers consume it.
/// Business logic should not depend on HTTP session, Windows principal, or any
/// specific authentication technology directly — only on this abstraction.
/// </summary>
public interface ICurrentUser
{
    /// <summary>
    /// The unique identifier of the currently authenticated user.
    /// </summary>
    Guid UserId { get; }

    /// <summary>
    /// The display name of the currently authenticated user.
    /// </summary>
    string UserName { get; }

    /// <summary>
    /// Returns true if the current user has the specified permission.
    /// Permission identifiers follow the convention: "module.action" (e.g., "catalog.products.create").
    /// </summary>
    bool HasPermission(string permission);

    /// <summary>
    /// Returns true if the current user's license includes the specified feature entitlement.
    /// </summary>
    bool HasFeature(FeatureId featureId);

    /// <summary>
    /// Returns true if a user is currently authenticated.
    /// </summary>
    bool IsAuthenticated { get; }
}
