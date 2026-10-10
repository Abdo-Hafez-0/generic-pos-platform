using CloudBackup.Domain;
using Platform.Application.Abstractions.Authorization;
using Platform.Core.Results;

namespace CloudBackup.Application;

// The user-facing entry points (backup.cloud.manage, owned by the module cloud-backup, so it needs the cloud-backup license). Each checks the capability before anything
// is made, stored or shown. Restoring from the cloud (backup.cloud.restore) comes with the screen in MISS-04e3.

public sealed record BeginRecoveryCodeCommand;

public sealed class BeginRecoveryCodeCommandHandler(CloudBackupService cloud, IAuthorizationService authorization)
{
    public async Task<Result<RecoveryCodeSetup>> HandleAsync(BeginRecoveryCodeCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(CloudBackupCapabilities.Manage, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<RecoveryCodeSetup>(allowed.Error);

        return cloud.BeginRecoveryCode();
    }
}

public sealed record ConfirmRecoveryCodeCommand(Guid SetupId, IReadOnlyList<string> TypedGroups);

public sealed class ConfirmRecoveryCodeCommandHandler(CloudBackupService cloud, IAuthorizationService authorization)
{
    public async Task<Result<CloudKeyInfo>> HandleAsync(ConfirmRecoveryCodeCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(CloudBackupCapabilities.Manage, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<CloudKeyInfo>(allowed.Error);

        return await cloud.ConfirmRecoveryCodeAsync(command.SetupId, command.TypedGroups, cancellationToken);
    }
}

public sealed record SetBackupAccessCommand(string? AccessKey);

public sealed class SetBackupAccessCommandHandler(CloudBackupService cloud, IAuthorizationService authorization)
{
    public async Task<Result> HandleAsync(SetBackupAccessCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(CloudBackupCapabilities.Manage, cancellationToken);
        if (allowed.IsFailure) return allowed;

        return await cloud.SetAccessAsync(command.AccessKey, cancellationToken);
    }
}

public sealed record GetCloudBackupOverviewQuery;

public sealed class GetCloudBackupOverviewQueryHandler(CloudBackupService cloud, IAuthorizationService authorization)
{
    public async Task<Result<CloudBackupOverview>> HandleAsync(GetCloudBackupOverviewQuery query, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(CloudBackupCapabilities.Manage, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<CloudBackupOverview>(allowed.Error);

        return await cloud.GetOverviewAsync(cancellationToken);
    }
}
