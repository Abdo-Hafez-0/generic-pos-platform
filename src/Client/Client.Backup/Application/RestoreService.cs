using Client.Backup.Domain;
using Microsoft.Extensions.Logging;
using Platform.Application.Abstractions.Auditing;
using Platform.Core.Results;

namespace Client.Backup.Application;

/// <summary>
/// Restores a backup across a restart (MISS-04b, design section 8). The running application holds the database open, so it is never
/// replaced in place:
///   1. <see cref="PrepareAsync"/> / <see cref="PrepareFromFileAsync"/> - copy the backup next to the database and check it: the
///      fingerprint from the history, a readable database, a backup of THIS application, and not from a newer version. Nothing changes.
///   2. <see cref="ConfirmAsync"/> - the person confirmed: the restore is recorded as pending (and audited in the current data).
///   3. The next start (<c>PendingRestoreStep</c>, before anything opens the database) keeps the current data as a before-restore copy
///      and puts the backup in its place; <see cref="GetLastOutcomeAsync"/> tells what happened.
/// Until the restart, <see cref="CancelAsync"/> takes it back. Permissions are checked by the handlers, not here.
/// </summary>
public sealed class RestoreService(
    IDatabaseSnapshotter snapshotter,
    ILocalDestinationFactory localDestinations,
    IBackupHistoryStore history,
    IRestoreStateStore states,
    IBackupWorkspace workspace,
    TimeProvider clock,
    BackupGate gate,
    ILogger<RestoreService> logger,
    IBusinessEventSink? events = null)
{
    private Prepared? _prepared;

    private sealed record Prepared(RestorePreparation Info, string StagedPath, string Sha256);

    // ------------------------------------------------------------------ prepare

    /// <summary>Prepares the restore of a backup from the history (its fingerprint must still match).</summary>
    public async Task<Result<RestorePreparation>> PrepareAsync(Guid backupId, CancellationToken cancellationToken = default)
    {
        var record = (await history.ReadAsync(cancellationToken)).FirstOrDefault(r => r.Id == backupId);
        if (record is null)
            return Error.NotFound(BackupErrorCodes.NotFound, "That backup is not in the backup history.");

        return await PrepareCoreAsync(record.Location, record, cancellationToken);
    }

    /// <summary>Prepares the restore of a backup file that is not in this PC's history (a new PC, a file from a USB drive).</summary>
    public async Task<Result<RestorePreparation>> PrepareFromFileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path.Trim()))
            return Error.Validation(BackupErrorCodes.NotFound, "Choose the backup file to restore.");

        return await PrepareCoreAsync(path.Trim(), null, cancellationToken);
    }

    private async Task<Result<RestorePreparation>> PrepareCoreAsync(string location, BackupRecord? record, CancellationToken cancellationToken)
    {
        if (!await gate.TryEnterAsync(cancellationToken))
            return Error.Conflict(BackupErrorCodes.Busy, "A backup is being made or checked. Try again when it has finished.");

        var staged = Path.Combine(workspace.RestoreDirectory, Guid.NewGuid().ToString("N") + ".db");
        var keep = false;
        try
        {
            if (await states.ReadPendingAsync(cancellationToken) is not null)
                return Error.Conflict(BackupErrorCodes.RestorePending, "A restore is already waiting for the application to restart. Restart it, or cancel that restore first.");

            var opened = await localDestinations.Create(Path.GetDirectoryName(location) ?? "").OpenReadAsync(location, cancellationToken);
            if (opened.IsFailure)
                return Result.Failure<RestorePreparation>(opened.Error);

            Directory.CreateDirectory(workspace.RestoreDirectory);
            var (size, sha256) = await BackupFileCopy.CopyAsync(opened.Value, staged, cancellationToken);
            var fileName = Path.GetFileName(location);

            if (record is not null && (size != record.SizeBytes || !string.Equals(sha256, record.Sha256, StringComparison.OrdinalIgnoreCase)))
                return Error.Failure(BackupErrorCodes.Damaged, $"The backup file {fileName} has changed or is damaged since it was made, so it cannot be restored.");

            var inspected = await snapshotter.InspectAsync(staged, cancellationToken);
            if (inspected.IsFailure)
                return Error.Failure(BackupErrorCodes.Damaged, $"The file {fileName} is not a readable database, so it cannot be restored.");

            if (inspected.Value.Migrations.Count == 0)
                return Error.Validation(BackupErrorCodes.NotABackup, $"The file {fileName} is not a backup of this application's data.");

            var live = await snapshotter.ReadLiveMigrationsAsync(cancellationToken);
            if (inspected.Value.Migrations.Except(live, StringComparer.Ordinal).Any())
                return Error.Validation(BackupErrorCodes.NewerVersion,
                    $"The backup {fileName} was made by a newer version of the application. Update this PC first, then restore it.");

            var createdAt = record?.CreatedAt ?? new DateTimeOffset(File.GetLastWriteTime(location));
            var info = new RestorePreparation(Guid.NewGuid(), fileName, createdAt, record?.ApplicationVersion ?? "unknown", record is not null);

            DiscardPrepared();
            _prepared = new Prepared(info, staged, sha256);
            keep = true;
            logger.LogInformation("Restore of {File} prepared; waiting for confirmation.", fileName);
            return info;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Preparing the restore of {Location} failed.", location);
            return Error.Failure(BackupErrorCodes.Failed, "The backup could not be prepared for restoring. Your data was not changed.");
        }
        finally
        {
            if (!keep) TryDelete(staged);
            gate.Exit();
        }
    }

    // ------------------------------------------------------------------ confirm, cancel

    /// <summary>
    /// The person confirmed the prepared restore: it is recorded as pending and happens at the next start. Audited here, in the data that
    /// is about to be replaced (the before-restore copy keeps that entry).
    /// </summary>
    public async Task<Result<PendingRestore>> ConfirmAsync(Guid preparationId, Guid? actorId, string? actorName, CancellationToken cancellationToken = default)
    {
        await gate.EnterAsync(cancellationToken);
        try
        {
            if (_prepared is not { } prepared || prepared.Info.Id != preparationId)
                return Error.NotFound(BackupErrorCodes.NothingPrepared, "That restore is no longer prepared. Choose the backup again.");

            if (await states.ReadPendingAsync(cancellationToken) is not null)
                return Error.Conflict(BackupErrorCodes.RestorePending, "A restore is already waiting for the application to restart.");

            var pending = new PendingRestore(prepared.Info.Id, Path.GetFileName(prepared.StagedPath), prepared.Sha256, prepared.Info.FileName,
                prepared.Info.BackupCreatedAt, clock.GetLocalNow(), actorId, actorName);
            await states.WritePendingAsync(pending, cancellationToken);
            _prepared = null;

            logger.LogWarning("Restore of {File} confirmed; it happens when the application next starts.", pending.SourceFileName);
            await events.TryRecordAsync(BusinessEvent.Create(BackupService.AuditModule, "backup.restore-requested", "backup", pending.Id.ToString(),
                $"Restore of backup {pending.SourceFileName} (made {pending.BackupCreatedAt:yyyy-MM-dd HH:mm}) confirmed; the data is replaced at the next start and the current data is kept."));
            return pending;
        }
        finally
        {
            gate.Exit();
        }
    }

    /// <summary>Takes back a prepared or confirmed restore (before the restart). Nothing was changed, so nothing needs undoing.</summary>
    public async Task<Result> CancelAsync(CancellationToken cancellationToken = default)
    {
        await gate.EnterAsync(cancellationToken);
        try
        {
            var hadPrepared = _prepared is not null;
            DiscardPrepared();

            var pending = await states.ReadPendingAsync(cancellationToken);
            if (pending is null)
                return hadPrepared ? Result.Success() : Error.NotFound(BackupErrorCodes.NothingPrepared, "There is no restore to cancel.");

            await states.ClearPendingAsync(cancellationToken);
            TryDelete(Path.Combine(workspace.RestoreDirectory, pending.StagedFileName));
            await events.TryRecordAsync(BusinessEvent.Create(BackupService.AuditModule, "backup.restore-cancelled", "backup", pending.Id.ToString(),
                $"Restore of backup {pending.SourceFileName} cancelled before the restart; nothing was changed."));
            return Result.Success();
        }
        finally
        {
            gate.Exit();
        }
    }

    // ------------------------------------------------------------------ status

    public Task<PendingRestore?> GetPendingAsync(CancellationToken cancellationToken = default) => states.ReadPendingAsync(cancellationToken);

    public Task<RestoreOutcome?> GetLastOutcomeAsync(CancellationToken cancellationToken = default) => states.ReadOutcomeAsync(cancellationToken);

    private void DiscardPrepared()
    {
        if (_prepared is { } old) TryDelete(old.StagedPath);
        _prepared = null;
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Temporary restore copy {Path} could not be removed; it is removed at the next start.", path);
        }
    }
}
