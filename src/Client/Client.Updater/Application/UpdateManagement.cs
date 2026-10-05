using Client.Updater.Domain;
using Platform.Application.Abstractions.Authorization;
using Platform.Core.Results;
using Updates.Contracts;

namespace Client.Updater.Application;

/// <summary>
/// The capability that guards downloading, installing and rolling back updates. Available in every license state: a security update
/// must be installable even when a license has lapsed (the package verifier still checks entitlements for licensed modules).
/// </summary>
public static class UpdatesCapabilities
{
    public const string Module = "updates";

    public const string Manage = "updates.manage";

    public static IReadOnlyList<CapabilityDescriptor> All { get; } =
    [
        new(Manage, Module, "Manage updates", "Download and install verified updates and roll an update back.", LicenseRequirement.None, IsSensitive: true)
    ];
}

public sealed class UpdatesCapabilityProvider : ICapabilityProvider
{
    public IReadOnlyCollection<CapabilityDescriptor> GetCapabilities() => UpdatesCapabilities.All;
}

/// <summary>
/// The user-facing entry points for changing what is installed. Each checks <c>updates.manage</c> BEFORE anything is fetched, staged or
/// switched; the package itself is still verified end to end (signature first) by <see cref="IUpdateService"/>, so permission never replaces
/// verification and verification never replaces permission. Discovery and startup recovery stay user-less (they change nothing).
/// </summary>
public sealed record DownloadUpdateCommand(UpdateInfo Update);

public sealed class DownloadUpdateCommandHandler(IUpdateService updates, IAuthorizationService authorization)
{
    public async Task<Result<string>> HandleAsync(DownloadUpdateCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(UpdatesCapabilities.Manage, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<string>(allowed.Error);

        return await updates.DownloadAsync(command.Update, cancellationToken);
    }
}

public sealed record InstallUpdateCommand(string PackagePath);

public sealed class InstallUpdateCommandHandler(IUpdateService updates, IAuthorizationService authorization)
{
    public async Task<Result<UpdateJournal>> HandleAsync(InstallUpdateCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(UpdatesCapabilities.Manage, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<UpdateJournal>(allowed.Error);

        return await updates.InstallAsync(command.PackagePath, cancellationToken);
    }
}

public sealed record RollbackUpdateCommand(string TargetId, bool RestoreData = false);

public sealed class RollbackUpdateCommandHandler(IUpdateService updates, IAuthorizationService authorization)
{
    public async Task<Result> HandleAsync(RollbackUpdateCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(UpdatesCapabilities.Manage, cancellationToken);
        if (allowed.IsFailure) return allowed;

        return await updates.RollbackAsync(command.TargetId, command.RestoreData, cancellationToken);
    }
}
