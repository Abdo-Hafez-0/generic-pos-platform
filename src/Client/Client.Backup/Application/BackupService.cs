using Client.Backup.Domain;
using Microsoft.Extensions.Logging;
using Platform.Application.Abstractions.Auditing;
using Platform.Core.Results;

namespace Client.Backup.Application;

/// <summary>Facts about the running application that a backup records.</summary>
public sealed record BackupRuntime(string ApplicationVersion);

/// <summary>
/// Makes, checks, lists and removes LOCAL backups of the business database (MISS-04a). One operation at a time: a second backup
/// requested while one is running is refused in plain words instead of queueing behind it. Every failure is a plain result (the
/// technical detail goes to the log), and the live database is never changed: a backup is a copy taken with the SQLite online
/// backup API while the application keeps running.
///
/// This service does NOT check permissions: the user-facing handlers (<see cref="CreateBackupCommandHandler"/> and friends) do,
/// before calling it. The scheduler (MISS-04c) calls it directly, as the application rather than as a person.
/// </summary>
public sealed class BackupService(
    IDatabaseSnapshotter snapshotter,
    ILocalDestinationFactory localDestinations,
    IBackupHistoryStore history,
    IBackupSettingsStore settings,
    IBackupWorkspace workspace,
    BackupRuntime runtime,
    TimeProvider clock,
    BackupGate gate,
    ILogger<BackupService> logger,
    IBusinessEventSink? events = null,
    IBackupStatusStore? status = null) : IBackupNoticeSource
{
    public const string AuditModule = "backup";

    /// <summary>The actor recorded for scheduled backups: the application did it, not whoever happens to be signed in.</summary>
    public const string ScheduledActor = "scheduled backup";

    /// <summary>Raised after every backup attempt and settings change (MISS-04c: the shell's notice follows it).</summary>
    public event EventHandler? Changed;

    // ------------------------------------------------------------------ make a backup

    public async Task<Result<BackupRecord>> CreateAsync(BackupOrigin origin, CancellationToken cancellationToken = default)
    {
        if (!await gate.TryEnterAsync(cancellationToken))
            return Error.Conflict(BackupErrorCodes.Busy, "A backup is already being made. Try again when it has finished.");

        var staged = Path.Combine(workspace.StagingDirectory, Guid.NewGuid().ToString("N") + ".db");
        Result<BackupRecord> result;
        try
        {
            var current = await settings.ReadAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(current.LocalFolder))
                return Error.Validation(BackupErrorCodes.NotConfigured, "No backup folder has been chosen yet, so no backup was made.");

            result = await MakeAsync(current, origin, staged, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Backup failed.");
            result = Error.Failure(BackupErrorCodes.Failed, "The backup could not be made. Your data was not changed.");
        }
        finally
        {
            TryDelete(staged);
            gate.Exit();
        }

        await RecordAttemptAsync(origin, result);
        return result;
    }

    private async Task<Result<BackupRecord>> MakeAsync(BackupSettings current, BackupOrigin origin, string staged, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workspace.StagingDirectory);
        var snapshot = await snapshotter.SnapshotAsync(staged, cancellationToken);
        if (snapshot.IsFailure)
            return Result.Failure<BackupRecord>(snapshot.Error);

        var takenAt = clock.GetLocalNow();
        var destination = localDestinations.Create(current.LocalFolder!);
        var stored = await destination.StoreAsync(staged, $"genericpos-{takenAt:yyyyMMdd-HHmmss}.db", cancellationToken);
        if (stored.IsFailure)
            return Result.Failure<BackupRecord>(stored.Error);

        var record = new BackupRecord(
            Guid.NewGuid(), Path.GetFileName(stored.Value), destination.Kind, stored.Value, takenAt, snapshot.Value.SizeBytes,
            snapshot.Value.Sha256, runtime.ApplicationVersion, snapshot.Value.Migrations, origin);

        var records = (await history.ReadAsync(cancellationToken)).ToList();
        records.Add(record);
        var expired = await ApplyRetentionAsync(records, current, cancellationToken);
        await history.WriteAsync(records, CancellationToken.None);

        logger.LogInformation("Backup {File} made ({Bytes} bytes, {Origin}).", record.FileName, record.SizeBytes, origin);
        await events.TryRecordAsync(As(origin, BusinessEvent.Create(AuditModule, "backup.created", "backup", record.Id.ToString(),
            $"Backup {record.FileName} made ({FormatSize(record.SizeBytes)}, {origin.ToString().ToLowerInvariant()}).",
            $"destination={record.Destination};migrations={record.Migrations.Count}")));
        foreach (var old in expired)
            await events.TryRecordAsync(As(origin, BusinessEvent.Create(AuditModule, "backup.expired", "backup", old.Id.ToString(),
                $"Old backup {old.FileName} removed (only the newest {current.KeepLocal} are kept).")));

        return record;
    }

    /// <summary>Scheduled work is the application's, never the signed-in person's.</summary>
    private static BusinessEvent As(BackupOrigin origin, BusinessEvent businessEvent)
        => origin == BackupOrigin.Scheduled ? businessEvent with { ActorName = ScheduledActor } : businessEvent;

    /// <summary>Remembers the attempt (the shell's notice, the scheduler's retry pause) and tells the shell. Never fails the backup.</summary>
    private async Task RecordAttemptAsync(BackupOrigin origin, Result<BackupRecord> result)
    {
        try
        {
            if (status is not null)
                await status.WriteAsync(new BackupStatus(clock.GetLocalNow(), origin, result.IsSuccess, result.IsFailure ? result.Error.Description : null));
            if (result.IsFailure)
                await events.TryRecordAsync(As(origin, BusinessEvent.Create(AuditModule, "backup.failed", "backup", null, result.Error.Description)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "The outcome of the backup attempt could not be recorded.");
        }

        RaiseChanged();
    }

    // ------------------------------------------------------------------ notice (MISS-04c)

    /// <summary>
    /// Null when all is well. No folder chosen comes first (nothing is being backed up at all); otherwise the last attempt, if it failed.
    /// A later successful backup clears the notice.
    /// </summary>
    public async Task<BackupNotice?> GetNoticeAsync(CancellationToken cancellationToken = default)
    {
        var current = await settings.ReadAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(current.LocalFolder))
            return new BackupNotice(BackupNoticeKind.NotConfigured);

        var last = status is null ? null : await status.ReadAsync(cancellationToken);
        return last is { LastAttemptSucceeded: false }
            ? new BackupNotice(BackupNoticeKind.LastBackupFailed, last.LastAttemptAt, last.LastFailure)
            : null;
    }

    /// <summary>The last backup attempt, if any (the scheduler waits before retrying a failed one).</summary>
    public async Task<BackupStatus?> GetLastAttemptAsync(CancellationToken cancellationToken = default)
        => status is null ? null : await status.ReadAsync(cancellationToken);

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // A listener's fault never turns a finished backup into a failure.
            logger.LogWarning(ex, "A listener of backup changes failed.");
        }
    }

    /// <summary>Keeps the newest <see cref="BackupSettings.KeepLocal"/> backups of the CURRENT folder; backups elsewhere are left alone.</summary>
    private async Task<List<BackupRecord>> ApplyRetentionAsync(List<BackupRecord> records, BackupSettings current, CancellationToken cancellationToken)
    {
        var folder = NormalizeFolder(current.LocalFolder!);
        var surplus = records
            .Where(r => r.Destination == BackupDestinations.Local && NormalizeFolder(Path.GetDirectoryName(r.Location) ?? "") == folder)
            .OrderByDescending(r => r.CreatedAt)
            .Skip(current.KeepLocal)
            .ToList();

        var removed = new List<BackupRecord>();
        foreach (var old in surplus)
        {
            var deleted = await localDestinations.Create(current.LocalFolder!).DeleteAsync(old.Location, cancellationToken);
            if (deleted.IsFailure)
            {
                logger.LogWarning("Old backup {File} could not be removed: {Reason}", old.FileName, deleted.Error.Description);
                continue;
            }

            records.Remove(old);
            removed.Add(old);
        }

        return removed;
    }

    // ------------------------------------------------------------------ verify

    /// <summary>
    /// Reads a backup back from its destination and checks it: same size and fingerprint as when it was made, and a database that passes
    /// its integrity check. The result is kept in the history. A backup that fails is reported as a failure (and marked in the history).
    /// </summary>
    public async Task<Result<BackupRecord>> VerifyAsync(Guid backupId, CancellationToken cancellationToken = default)
    {
        await gate.EnterAsync(cancellationToken);
        var staged = Path.Combine(workspace.StagingDirectory, Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var records = (await history.ReadAsync(cancellationToken)).ToList();
            var index = records.FindIndex(r => r.Id == backupId);
            if (index < 0)
                return Error.NotFound(BackupErrorCodes.NotFound, "That backup is not in the backup history.");

            var record = records[index];
            var problem = await CheckAsync(record, staged, cancellationToken);

            var checkedRecord = record with { LastVerifiedAt = clock.GetLocalNow(), LastVerifyPassed = problem is null };
            records[index] = checkedRecord;
            await history.WriteAsync(records, CancellationToken.None);

            await events.TryRecordAsync(BusinessEvent.Create(AuditModule, problem is null ? "backup.verified" : "backup.verify-failed", "backup",
                record.Id.ToString(), problem is null ? $"Backup {record.FileName} checked: it is complete and readable." : $"Backup {record.FileName} failed its check."));

            return problem is null ? checkedRecord : Result.Failure<BackupRecord>(problem);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Verifying backup {Id} failed.", backupId);
            return Error.Failure(BackupErrorCodes.Failed, "The backup could not be checked.");
        }
        finally
        {
            TryDelete(staged);
            gate.Exit();
        }
    }

    /// <summary>Null when the backup is good; otherwise the plain reason.</summary>
    private async Task<Error?> CheckAsync(BackupRecord record, string staged, CancellationToken cancellationToken)
    {
        var destination = localDestinations.Create(Path.GetDirectoryName(record.Location) ?? "");
        var opened = await destination.OpenReadAsync(record.Location, cancellationToken);
        if (opened.IsFailure)
            return opened.Error;

        Directory.CreateDirectory(workspace.StagingDirectory);
        var (size, sha256) = await BackupFileCopy.CopyAsync(opened.Value, staged, cancellationToken);

        if (size != record.SizeBytes || !string.Equals(sha256, record.Sha256, StringComparison.OrdinalIgnoreCase))
            return Error.Failure(BackupErrorCodes.Damaged, $"The backup file {record.FileName} has changed or is damaged since it was made. Do not rely on it.");

        var inspected = await snapshotter.InspectAsync(staged, cancellationToken);
        return inspected.IsFailure
            ? Error.Failure(BackupErrorCodes.Damaged, $"The backup file {record.FileName} is not a readable database. Do not rely on it.")
            : null;
    }

    // ------------------------------------------------------------------ history, delete

    public async Task<IReadOnlyList<BackupRecord>> GetHistoryAsync(CancellationToken cancellationToken = default)
        => (await history.ReadAsync(cancellationToken)).OrderByDescending(r => r.CreatedAt).ToList();

    public async Task<Result> DeleteAsync(Guid backupId, CancellationToken cancellationToken = default)
    {
        await gate.EnterAsync(cancellationToken);
        try
        {
            var records = (await history.ReadAsync(cancellationToken)).ToList();
            var record = records.FirstOrDefault(r => r.Id == backupId);
            if (record is null)
                return Error.NotFound(BackupErrorCodes.NotFound, "That backup is not in the backup history.");

            var deleted = await localDestinations.Create(Path.GetDirectoryName(record.Location) ?? "").DeleteAsync(record.Location, cancellationToken);
            if (deleted.IsFailure)
                return deleted;

            records.Remove(record);
            await history.WriteAsync(records, CancellationToken.None);
            await events.TryRecordAsync(BusinessEvent.Create(AuditModule, "backup.deleted", "backup", record.Id.ToString(), $"Backup {record.FileName} deleted."));
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Deleting backup {Id} failed.", backupId);
            return Error.Failure(BackupErrorCodes.Failed, "The backup could not be deleted.");
        }
        finally
        {
            gate.Exit();
        }
    }

    // ------------------------------------------------------------------ settings

    public Task<BackupSettings> GetSettingsAsync(CancellationToken cancellationToken = default) => settings.ReadAsync(cancellationToken);

    /// <summary>
    /// Changes the folder and how many backups it keeps. The folder must be a full path the application can write to (checked now, with a
    /// small test file, so a wrong choice is found today and not at 23:00). An empty folder turns local backups off.
    /// </summary>
    public async Task<Result<BackupSettings>> UpdateSettingsAsync(string? localFolder, int keepLocal, bool scheduleEnabled = true, TimeOnly? dailyAt = null,
        CancellationToken cancellationToken = default)
    {
        if (keepLocal is < BackupSettings.MinKeepLocal or > BackupSettings.MaxKeepLocal)
            return Error.Validation(BackupErrorCodes.InvalidSettings, $"Keep between {BackupSettings.MinKeepLocal} and {BackupSettings.MaxKeepLocal} backups.");

        var folder = string.IsNullOrWhiteSpace(localFolder) ? null : localFolder.Trim();
        if (folder is not null)
        {
            if (!Path.IsPathFullyQualified(folder))
                return Error.Validation(BackupErrorCodes.InvalidSettings, "Enter the full path of the backup folder, for example E:\\Backups.");

            if (NormalizeFolder(folder).StartsWith(NormalizeFolder(workspace.StagingDirectory), StringComparison.OrdinalIgnoreCase))
                return Error.Validation(BackupErrorCodes.InvalidSettings, "Choose a folder outside the application's own working folder.");

            var writable = LocalFolderProbe.CanWrite(folder, logger);
            if (writable.IsFailure)
                return Result.Failure<BackupSettings>(writable.Error);
        }

        await gate.EnterAsync(cancellationToken);
        try
        {
            var updated = new BackupSettings(folder, keepLocal, scheduleEnabled, dailyAt);
            await settings.WriteAsync(updated, cancellationToken);
            await events.TryRecordAsync(BusinessEvent.Create(AuditModule, "backup.settings-changed", "backup-settings", null,
                folder is null ? "Local backups turned off (no backup folder)."
                    : $"Backups go to {folder}; the newest {keepLocal} are kept; " +
                      (scheduleEnabled ? $"a backup is made every day at {updated.ScheduledTime():HH\\:mm}." : "no daily backup.")));
            RaiseChanged();
            return updated;
        }
        finally
        {
            gate.Exit();
        }
    }

    // ------------------------------------------------------------------ helpers

    private static string NormalizeFolder(string folder)
        => folder.Length == 0 ? folder : Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    private static string FormatSize(long bytes)
        => bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024.0):0.0} MB" : $"{Math.Max(1, bytes / 1024)} KB";

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Temporary backup copy {Path} could not be removed; it is removed at the next start.", path);
        }
    }
}

/// <summary>Checks that the application can create and remove a file in a folder (the folder is created if missing).</summary>
public static class LocalFolderProbe
{
    public static Result CanWrite(string folder, ILogger logger)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, $".genericpos-write-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "This file checks that backups can be written here. It is removed at once.");
            File.Delete(probe);
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            logger.LogWarning(ex, "Backup folder {Folder} is not writable.", folder);
            return Error.Failure(BackupErrorCodes.DestinationUnavailable,
                $"Backups cannot be written to {folder}. Check that the drive is connected and that the folder may be written to.");
        }
    }
}

/// <summary>Copies a backup stream to a file and fingerprints it on the way (one pass, never the whole file in memory).</summary>
public static class BackupFileCopy
{
    public static async Task<(long Size, string Sha256)> CopyAsync(Stream source, string targetPath, CancellationToken cancellationToken)
    {
        await using (source)
        await using (var target = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256))
        {
            var buffer = new byte[81920];
            long size = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                size += read;
            }

            await target.FlushAsync(cancellationToken);
            return (size, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
    }
}
