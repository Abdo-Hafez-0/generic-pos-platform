using CloudBackup.Domain;
using Platform.Application.Abstractions.Authorization;
using Platform.Core.Results;

namespace CloudBackup.Application;

// The user-facing entry points. Setting up (backup.cloud.manage) is owned by the module cloud-backup, so it needs the cloud-backup license;
// restoring (backup.cloud.restore) needs no license (rule 10). Each checks its capability before anything is made, stored, read or shown.

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

public sealed record ListCloudBackupsQuery;

public sealed class ListCloudBackupsQueryHandler(CloudBackupService cloud, IAuthorizationService authorization)
{
    public async Task<Result<IReadOnlyList<CloudBackup.Contracts.CloudBackupItem>>> HandleAsync(ListCloudBackupsQuery query, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(CloudBackupCapabilities.Restore, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<IReadOnlyList<CloudBackup.Contracts.CloudBackupItem>>(allowed.Error);

        return await cloud.ListCloudAsync(cancellationToken);
    }
}

public sealed record DownloadForRestoreCommand(Guid BackupId, string? RecoveryCode, string? Label = null);

public sealed class DownloadForRestoreCommandHandler(CloudBackupService cloud, IAuthorizationService authorization)
{
    public async Task<Result<DownloadedBackup>> HandleAsync(DownloadForRestoreCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(CloudBackupCapabilities.Restore, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<DownloadedBackup>(allowed.Error);

        return await cloud.DownloadForRestoreAsync(command.BackupId, command.RecoveryCode, command.Label, cancellationToken);
    }
}

public sealed record DiscardDownloadCommand(string PlainPath);

/// <summary>Removes the plain copy once the local restore has taken its own copy (or the person gave up).</summary>
public sealed class DiscardDownloadCommandHandler(CloudBackupService cloud, IAuthorizationService authorization)
{
    public async Task<Result> HandleAsync(DiscardDownloadCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(CloudBackupCapabilities.Restore, cancellationToken);
        if (allowed.IsFailure) return allowed;

        cloud.DiscardDownload(command.PlainPath);
        return Result.Success();
    }
}

// ---- recovery through the vendor (MISS-04f): part of restoring, so backup.cloud.restore (no license needed, rule 10)

public sealed record CreateRecoveryRequestCommand(Guid BackupId, string? Label, string? LicenseId);

public sealed class CreateRecoveryRequestCommandHandler(CloudBackupService cloud, IAuthorizationService authorization)
{
    public async Task<Result<RecoveryRequestMade>> HandleAsync(CreateRecoveryRequestCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(CloudBackupCapabilities.Restore, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<RecoveryRequestMade>(allowed.Error);

        return await cloud.CreateRecoveryRequestAsync(command.BackupId, command.Label, command.LicenseId, cancellationToken);
    }
}

public sealed record DownloadWithRestoreKeyCommand(string? RestoreKey);

public sealed class DownloadWithRestoreKeyCommandHandler(CloudBackupService cloud, IAuthorizationService authorization)
{
    public async Task<Result<DownloadedBackup>> HandleAsync(DownloadWithRestoreKeyCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(CloudBackupCapabilities.Restore, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<DownloadedBackup>(allowed.Error);

        return await cloud.DownloadWithRestoreKeyAsync(command.RestoreKey, cancellationToken);
    }
}

/// <summary>The restore opened with the vendor's restore key was confirmed (Confirmed = true) or called off before the restart (false).</summary>
public sealed record VendorRestoreDecidedCommand(Guid BackupId, bool Confirmed);

public sealed class VendorRestoreDecidedCommandHandler(CloudBackupService cloud, IAuthorizationService authorization)
{
    public async Task<Result> HandleAsync(VendorRestoreDecidedCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(CloudBackupCapabilities.Restore, cancellationToken);
        if (allowed.IsFailure) return allowed;

        if (command.Confirmed) await cloud.VendorRestoreConfirmedAsync(command.BackupId, cancellationToken);
        else await cloud.VendorRestoreCancelledAsync(command.BackupId, cancellationToken);
        return Result.Success();
    }
}
