using Platform.Application.Abstractions.Security;
using Platform.Core.Results;

namespace Platform.Application.Abstractions.Authorization;

/// <summary>
/// Default <see cref="IAuthorizationService"/>: known capability? signed in? does the user CURRENTLY hold it? Fails closed at every step.
/// Denials are reported to <see cref="ISecurityEventSink"/> so that attempts to cross the boundary are visible in the audit trail.
/// </summary>
public sealed class AuthorizationService(
    ICurrentUser currentUser,
    ICapabilityCatalog catalog,
    IPermissionProvider? permissions = null,
    ISecurityEventSink? events = null) : IAuthorizationService
{
    public async Task<Result> AuthorizeAsync(string capability, CancellationToken cancellationToken = default)
    {
        var decision = await DecideAsync(capability, cancellationToken);
        if (decision.IsSuccess)
            return decision;

        if (events is not null)
            await events.RecordAsync(SecurityEvent.Create(
                "security.authorization.denied", SecurityEventOutcome.Denied,
                currentUser.IsAuthenticated ? currentUser.UserId : null,
                currentUser.IsAuthenticated ? currentUser.UserName : null,
                subjectType: "capability", subjectId: capability, summary: decision.Error.Code), cancellationToken);

        return decision;
    }

    public async Task<bool> IsAllowedAsync(string capability, CancellationToken cancellationToken = default)
        => (await DecideAsync(capability, cancellationToken)).IsSuccess;

    private async Task<Result> DecideAsync(string capability, CancellationToken cancellationToken)
    {
        if (catalog.Find(capability) is not { } descriptor)
            return SecurityErrors.UnknownCapability(capability);

        if (!currentUser.IsAuthenticated)
            return SecurityErrors.NotAuthenticated();

        if (permissions is null)
            return SecurityErrors.Forbidden(descriptor.Code);

        var held = await permissions.GetPermissionsAsync(currentUser.UserId, cancellationToken);
        return held.Contains(descriptor.Code, StringComparer.OrdinalIgnoreCase)
            ? Result.Success()
            : SecurityErrors.Forbidden(descriptor.Code);
    }
}
