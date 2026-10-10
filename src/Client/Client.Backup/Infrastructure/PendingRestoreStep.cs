using System.Security.Cryptography;
using Client.Backup.Application;
using Client.Backup.Domain;
using Client.Host.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Client.Backup.Infrastructure;

/// <summary>
/// Puts a confirmed backup in place of the business database at start, BEFORE anything opens it (MISS-04b, design section 8 step 5):
///   1. the staged backup must still have the fingerprint recorded when the restore was confirmed;
///   2. the current database file and its side files (-wal, -shm, -journal) are MOVED together into before-restore\ under a new name
///      (moving them together keeps every committed write, including those still in the WAL);
///   3. the staged backup is moved into the database's place.
/// If step 2 or 3 fails, everything moved is put back: the shop starts with its data exactly as before and the outcome says so.
/// Afterwards the before-restore copy is folded into one self-contained file and listed in the backup history, so a wrong restore can
/// be undone by restoring it. It is never removed automatically. The pending marker is always cleared: a failed restore is not retried
/// behind the person's back.
/// </summary>
public sealed class PendingRestoreStep(
    string databasePath,
    BackupWorkspace workspace,
    IRestoreStateStore states,
    IBackupHistoryStore history,
    IDatabaseSnapshotter snapshotter,
    TimeProvider clock,
    ILogger<PendingRestoreStep> logger) : IStartupPreparation
{
    private static readonly string[] SideFileSuffixes = ["-wal", "-shm", "-journal"];

    public async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        var pending = await states.ReadPendingAsync(cancellationToken);
        if (pending is null)
        {
            SweepRestoreDirectory();
            return;
        }

        var outcome = await ApplyAsync(pending, cancellationToken);
        await states.WriteOutcomeAsync(outcome, CancellationToken.None);
        await states.ClearPendingAsync(CancellationToken.None);
        SweepRestoreDirectory();

        if (outcome.Succeeded)
            logger.LogWarning("Shop data restored from backup {File}; the previous data was kept as {Before}.", outcome.SourceFileName, outcome.BeforeRestoreFileName);
        else
            logger.LogError("The restore of backup {File} did not happen: {Message}", outcome.SourceFileName, outcome.Message);
    }

    private async Task<RestoreOutcome> ApplyAsync(PendingRestore pending, CancellationToken cancellationToken)
    {
        RestoreOutcome Outcome(bool succeeded, string message, string? before = null) => new(
            pending.Id, succeeded, message, pending.SourceFileName, pending.BackupCreatedAt, before, clock.GetLocalNow(), pending.RequestedById, pending.RequestedByName);

        var staged = Path.Combine(workspace.RestoreDirectory, pending.StagedFileName);
        if (!StagedBackupIsIntact(staged, pending.Sha256))
            return Outcome(false, "The prepared backup was missing or had changed, so nothing was restored. Your data was not changed.");

        try
        {
            Directory.CreateDirectory(workspace.BeforeRestoreDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "The folder for the data before the restore could not be created.");
            return Outcome(false, "The folder that keeps a copy of the current data could not be created, so nothing was restored. Your data was not changed.");
        }

        var beforePath = UniqueBeforeRestorePath();
        var moved = new List<(string From, string To)>();
        try
        {
            foreach (var suffix in new[] { "" }.Concat(SideFileSuffixes))
            {
                var from = databasePath + suffix;
                if (!File.Exists(from)) continue;
                var to = beforePath + suffix;
                File.Move(from, to);
                moved.Add((from, to));
            }

            File.Move(staged, databasePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Putting backup {File} in place failed; the current data is put back.", pending.SourceFileName);
            var undone = PutBack(moved);
            return Outcome(false, undone
                ? "The shop data was in use (is another copy of the application open?), so nothing was restored. Your data was not changed. Close other copies and restore again."
                : $"The restore failed and the previous data could not be put back automatically. It is safe in {workspace.BeforeRestoreDirectory}; contact support before using the application.");
        }

        var beforeName = moved.Count > 0 ? Path.GetFileName(beforePath) : null;
        try
        {
            if (moved.Count > 0)
                await ListBeforeRestoreCopyAsync(beforePath, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // The restore itself is done and the copy is safe on disk; only its line in the history is missing.
            logger.LogWarning(ex, "The data kept before the restore ({Path}) could not be added to the backup history.", beforePath);
        }

        return Outcome(true,
            beforeName is null
                ? $"The shop data was restored from the backup {pending.SourceFileName}."
                : $"The shop data was restored from the backup {pending.SourceFileName}. The data as it was before the restore is kept as {beforeName}.",
            beforeName);
    }

    /// <summary>Moves back what was moved, newest first. False when something could not be put back (the files stay in before-restore).</summary>
    private bool PutBack(List<(string From, string To)> moved)
    {
        var ok = true;
        foreach (var (from, to) in Enumerable.Reverse(moved))
        {
            try
            {
                File.Move(to, from);   // never deletes: if something now occupies the place, the data stays safe in before-restore
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogCritical(ex, "Could not put {File} back after a failed restore.", from);
                ok = false;
            }
        }

        return ok;
    }

    /// <summary>
    /// Folds the WAL into the before-restore copy (one self-contained file that any SQLite tool opens and that can itself be restored) and
    /// lists it in the history. If the copy cannot be read (the reason for restoring may be a damaged database), it is kept as it is.
    /// </summary>
    private async Task ListBeforeRestoreCopyAsync(string beforePath, CancellationToken cancellationToken)
    {
        try
        {
            using (var connection = new SqliteConnection($"Data Source={beforePath};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE;";
                command.ExecuteNonQuery();
            }

            foreach (var suffix in SideFileSuffixes)
                if (File.Exists(beforePath + suffix) && new FileInfo(beforePath + suffix).Length == 0)
                    File.Delete(beforePath + suffix);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "The data kept before the restore could not be folded into one file; it is kept as it was ({Path}).", beforePath);
        }

        var inspected = await snapshotter.InspectAsync(beforePath, cancellationToken);
        var record = new BackupRecord(
            Guid.NewGuid(), Path.GetFileName(beforePath), BackupDestinations.BeforeRestore, beforePath, clock.GetLocalNow(),
            new FileInfo(beforePath).Length, inspected.IsSuccess ? inspected.Value.Sha256 : Fingerprint(beforePath), "before restore",
            inspected.IsSuccess ? inspected.Value.Migrations : [], BackupOrigin.Manual,
            inspected.IsSuccess ? null : clock.GetLocalNow(), inspected.IsSuccess ? null : false);

        var records = (await history.ReadAsync(cancellationToken)).ToList();
        records.Add(record);
        await history.WriteAsync(records, CancellationToken.None);
    }

    private string UniqueBeforeRestorePath()
    {
        var stem = $"before-restore-{clock.GetLocalNow():yyyyMMdd-HHmmss}";
        var path = Path.Combine(workspace.BeforeRestoreDirectory, stem + ".db");
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(workspace.BeforeRestoreDirectory, $"{stem}-{n}.db");
        return path;
    }

    private bool StagedBackupIsIntact(string staged, string sha256)
    {
        try
        {
            return File.Exists(staged) && string.Equals(Fingerprint(staged), sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "The prepared backup {Path} could not be read.", staged);
            return false;
        }
    }

    private static string Fingerprint(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>Prepared-but-never-confirmed copies (the application was closed in between) and used ones are removed.</summary>
    private void SweepRestoreDirectory()
    {
        try
        {
            if (!Directory.Exists(workspace.RestoreDirectory)) return;
            foreach (var file in Directory.EnumerateFiles(workspace.RestoreDirectory))
                File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Leftover restore copies could not be removed.");
        }
    }
}
