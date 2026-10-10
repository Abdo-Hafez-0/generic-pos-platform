using Client.Backup.Domain;
using Platform.Application.Abstractions.Authorization;
using Platform.Core.Results;

namespace Client.Backup.Application;

/// <summary>
/// The backup capabilities (design section 7). None needs a license: backing up, restoring and removing your own data must keep working
/// in every license state (rule 10). The cloud destination of the optional CloudBackup module is licensed separately.
/// </summary>
public static class BackupCapabilities
{
    public const string Module = "backup";

    public const string Create = "backup.create";
    public const string Restore = "backup.restore";
    public const string Delete = "backup.delete";
    public const string Configure = "backup.configure";

    public static IReadOnlyList<CapabilityDescriptor> All { get; } =
    [
        new(Create, Module, "Make backups", "Make a backup now, check a backup and see the backup history.", LicenseRequirement.None),
        new(Restore, Module, "Restore backups", "Replace the shop's data with a backup.", LicenseRequirement.None, IsSensitive: true),
        new(Delete, Module, "Delete backups", "Delete backups.", LicenseRequirement.None, IsSensitive: true),
        new(Configure, Module, "Backup settings", "Choose where backups go and how many are kept.", LicenseRequirement.None, IsSensitive: true)
    ];
}

public sealed class BackupCapabilityProvider : ICapabilityProvider
{
    public IReadOnlyCollection<CapabilityDescriptor> GetCapabilities() => BackupCapabilities.All;
}

// The user-facing entry points. Each checks its capability BEFORE anything is read, copied or removed.

public sealed record CreateBackupCommand;

public sealed class CreateBackupCommandHandler(BackupService backups, IAuthorizationService authorization)
{
    public async Task<Result<BackupRecord>> HandleAsync(CreateBackupCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(BackupCapabilities.Create, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<BackupRecord>(allowed.Error);

        return await backups.CreateAsync(BackupOrigin.Manual, cancellationToken);
    }
}

public sealed record VerifyBackupCommand(Guid BackupId);

public sealed class VerifyBackupCommandHandler(BackupService backups, IAuthorizationService authorization)
{
    public async Task<Result<BackupRecord>> HandleAsync(VerifyBackupCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(BackupCapabilities.Create, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<BackupRecord>(allowed.Error);

        return await backups.VerifyAsync(command.BackupId, cancellationToken);
    }
}

public sealed record GetBackupHistoryQuery;

public sealed class GetBackupHistoryQueryHandler(BackupService backups, IAuthorizationService authorization)
{
    public async Task<Result<IReadOnlyList<BackupRecord>>> HandleAsync(GetBackupHistoryQuery query, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(BackupCapabilities.Create, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<IReadOnlyList<BackupRecord>>(allowed.Error);

        return Result.Success(await backups.GetHistoryAsync(cancellationToken));
    }
}

public sealed record DeleteBackupCommand(Guid BackupId);

public sealed class DeleteBackupCommandHandler(BackupService backups, IAuthorizationService authorization)
{
    public async Task<Result> HandleAsync(DeleteBackupCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(BackupCapabilities.Delete, cancellationToken);
        if (allowed.IsFailure) return allowed;

        return await backups.DeleteAsync(command.BackupId, cancellationToken);
    }
}

public sealed record GetBackupSettingsQuery;

/// <summary>Anyone who may make backups may see where they go (the status line of the backup screen).</summary>
public sealed class GetBackupSettingsQueryHandler(BackupService backups, IAuthorizationService authorization)
{
    public async Task<Result<BackupSettings>> HandleAsync(GetBackupSettingsQuery query, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(BackupCapabilities.Create, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<BackupSettings>(allowed.Error);

        return Result.Success(await backups.GetSettingsAsync(cancellationToken));
    }
}

public sealed record UpdateBackupSettingsCommand(string? LocalFolder, int KeepLocal);

public sealed class UpdateBackupSettingsCommandHandler(BackupService backups, IAuthorizationService authorization)
{
    public async Task<Result<BackupSettings>> HandleAsync(UpdateBackupSettingsCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(BackupCapabilities.Configure, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<BackupSettings>(allowed.Error);

        return await backups.UpdateSettingsAsync(command.LocalFolder, command.KeepLocal, cancellationToken);
    }
}
