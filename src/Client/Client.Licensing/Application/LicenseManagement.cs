using Client.Licensing.Domain;
using Platform.Application.Abstractions.Authorization;
using Platform.Core.Results;

namespace Client.Licensing.Application;

/// <summary>
/// The capability that guards activating or renewing the installation's license. Always available, whatever the license state:
/// an expired or missing license must be fixable (and that is only possible by someone allowed to manage it).
/// </summary>
public static class LicensingCapabilities
{
    public const string Module = "licensing";

    public const string Manage = "licensing.manage";

    public static IReadOnlyList<CapabilityDescriptor> All { get; } =
    [
        new(Manage, Module, "Manage the license", "Activate this installation with an activation key and renew its license.",
            LicenseRequirement.None, IsSensitive: true)
    ];
}

public sealed class LicensingCapabilityProvider : ICapabilityProvider
{
    public IReadOnlyCollection<CapabilityDescriptor> GetCapabilities() => LicensingCapabilities.All;
}

/// <summary>Activates this installation. The user-facing entry point: <c>licensing.manage</c> is checked BEFORE the activation key is sent anywhere.</summary>
public sealed record ActivateLicenseCommand(string ActivationKey);

public sealed class ActivateLicenseCommandHandler(ILicenseService licenses, IAuthorizationService authorization)
{
    public async Task<Result<LicenseEvaluation>> HandleAsync(ActivateLicenseCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(LicensingCapabilities.Manage, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<LicenseEvaluation>(allowed.Error);

        return await licenses.ActivateAsync(command.ActivationKey, cancellationToken);
    }
}

/// <summary>
/// "Renew now" for a person. (Automatic renewal runs inside the application without a user and talks to <see cref="ILicenseService"/>
/// directly: a renewal can never grant more than the server's signed answer, which is verified like any other license.)
/// </summary>
public sealed record RenewLicenseCommand;

public sealed class RenewLicenseCommandHandler(ILicenseService licenses, IAuthorizationService authorization)
{
    public async Task<Result<LicenseEvaluation>> HandleAsync(RenewLicenseCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(LicensingCapabilities.Manage, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<LicenseEvaluation>(allowed.Error);

        return await licenses.RenewAsync(cancellationToken);
    }
}
