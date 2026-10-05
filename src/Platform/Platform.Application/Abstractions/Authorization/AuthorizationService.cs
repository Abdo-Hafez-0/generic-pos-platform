using Platform.Application.Abstractions.Licensing;
using Platform.Application.Abstractions.Security;
using Platform.Core.Modules;
using Platform.Core.Results;

namespace Platform.Application.Abstractions.Authorization;

/// <summary>
/// Default <see cref="IAuthorizationService"/>: known capability? signed in? does the user CURRENTLY hold it? and, for capabilities
/// that need it, is the owning module LICENSED? Fails closed at every step. Denials are reported to <see cref="ISecurityEventSink"/>
/// so that attempts to cross the boundary are visible in the audit trail.
///
/// THIS is the one place license entitlements are enforced (no scattered checks in business code). The answer comes from the locally
/// verified license, offline, through <see cref="ILicenseEntitlementService"/>. When a host has no licensing component at all
/// (<paramref name="licensing"/> is null) there is nothing to enforce; the desktop always registers it, and there is deliberately no
/// setting that turns enforcement off. A refusal never touches data: it only declines to START a licensed operation, while capabilities
/// declared with <see cref="LicenseRequirement.None"/> (reading and exporting your own data, backup, user and license administration)
/// keep working in every license state.
/// </summary>
public sealed class AuthorizationService(
    ICurrentUser currentUser,
    ICapabilityCatalog catalog,
    IPermissionProvider? permissions = null,
    ISecurityEventSink? events = null,
    ILicenseEntitlementService? licensing = null) : IAuthorizationService
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
        if (!held.Contains(descriptor.Code, StringComparer.OrdinalIgnoreCase))
            return SecurityErrors.Forbidden(descriptor.Code);

        // Permission first: someone who may not do this anyway learns nothing about the license.
        if (licensing is not null
            && descriptor.License == LicenseRequirement.Module
            && !licensing.IsModuleLicensed(new ModuleId(descriptor.Module)))
            return SecurityErrors.LicenseRestricted(descriptor.Code, licensing.State.ToString());

        return Result.Success();
    }
}
