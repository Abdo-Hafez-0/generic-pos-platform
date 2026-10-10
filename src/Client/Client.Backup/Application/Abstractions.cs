using Client.Backup.Domain;
using Platform.Core.Results;

namespace Client.Backup.Application;

/// <summary>
/// Takes a consistent copy of the live business database while the application keeps running (SQLite online backup), checks it
/// (<c>PRAGMA integrity_check</c>) and fingerprints it. Never changes the live database.
/// </summary>
public interface IDatabaseSnapshotter
{
    /// <summary>Copies the live database to <paramref name="targetPath"/>. A copy that fails its check is deleted and reported.</summary>
    Task<Result<DatabaseSnapshot>> SnapshotAsync(string targetPath, CancellationToken cancellationToken = default);

    /// <summary>Checks a database file that is NOT in use (a staged copy of a backup): integrity check and migration list.</summary>
    Task<Result<DatabaseSnapshot>> InspectAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>The migrations applied in the LIVE database (read-only), to refuse restoring data from a newer application version.</summary>
    Task<IReadOnlyList<string>> ReadLiveMigrationsAsync(CancellationToken cancellationToken = default);
}

/// <summary>The last backup attempt (kept next to the database).</summary>
public interface IBackupStatusStore
{
    Task<BackupStatus?> ReadAsync(CancellationToken cancellationToken = default);

    Task WriteAsync(BackupStatus status, CancellationToken cancellationToken = default);
}

/// <summary>
/// What the shell shows about backups (MISS-04c): null when all is well. <see cref="Changed"/> is raised (on any thread) after a backup
/// attempt or a settings change, so the notice appears or disappears without anyone reopening a screen.
/// </summary>
public interface IBackupNoticeSource
{
    event EventHandler? Changed;

    Task<BackupNotice?> GetNoticeAsync(CancellationToken cancellationToken = default);
}

/// <summary>The confirmed restore waiting for the next start, and the outcome of the last one (both kept next to the database).</summary>
public interface IRestoreStateStore
{
    Task<PendingRestore?> ReadPendingAsync(CancellationToken cancellationToken = default);

    Task WritePendingAsync(PendingRestore pending, CancellationToken cancellationToken = default);

    Task ClearPendingAsync(CancellationToken cancellationToken = default);

    Task<RestoreOutcome?> ReadOutcomeAsync(CancellationToken cancellationToken = default);

    Task WriteOutcomeAsync(RestoreOutcome outcome, CancellationToken cancellationToken = default);
}

/// <summary>One backup operation at a time across making, checking, deleting and restoring.</summary>
public sealed class BackupGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public Task<bool> TryEnterAsync(CancellationToken cancellationToken = default) => _semaphore.WaitAsync(0, cancellationToken);

    public Task EnterAsync(CancellationToken cancellationToken = default) => _semaphore.WaitAsync(cancellationToken);

    public void Exit() => _semaphore.Release();
}

/// <summary>
/// Where finished backups are kept. Local backups go to a folder (a USB drive and a network share are folders too); the optional
/// CloudBackup module adds the cloud. A destination only stores, reads and removes files it was given: it never decides retention.
/// </summary>
public interface IBackupDestination
{
    /// <summary>"local", "cloud"...: the value kept in <see cref="BackupRecord.Destination"/>.</summary>
    string Kind { get; }

    /// <summary>Stores the finished file under <paramref name="fileName"/>; returns where it now is. Never overwrites an existing file.</summary>
    Task<Result<string>> StoreAsync(string sourcePath, string fileName, CancellationToken cancellationToken = default);

    /// <summary>Opens a stored backup for reading (verify, restore). NotFound when it is no longer there.</summary>
    Task<Result<Stream>> OpenReadAsync(string location, CancellationToken cancellationToken = default);

    /// <summary>Removes a stored backup. Success when it is already gone.</summary>
    Task<Result> DeleteAsync(string location, CancellationToken cancellationToken = default);
}

/// <summary>Chooses the local destination from the current settings (the folder can change at any time).</summary>
public interface ILocalDestinationFactory
{
    IBackupDestination Create(string folder);
}

/// <summary>The backup history, kept next to the database (not in it).</summary>
public interface IBackupHistoryStore
{
    Task<IReadOnlyList<BackupRecord>> ReadAsync(CancellationToken cancellationToken = default);

    Task WriteAsync(IReadOnlyList<BackupRecord> records, CancellationToken cancellationToken = default);
}

/// <summary>The backup settings, kept next to the database (not in it).</summary>
public interface IBackupSettingsStore
{
    Task<BackupSettings> ReadAsync(CancellationToken cancellationToken = default);

    Task WriteAsync(BackupSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>The working folders of the backup component.</summary>
public interface IBackupWorkspace
{
    /// <summary>Where copies are prepared and checked; emptied at start (a crash can leave a half-written copy behind).</summary>
    string StagingDirectory { get; }

    /// <summary>Where a backup being restored waits (checked, prepared or confirmed) until the next start puts it in place.</summary>
    string RestoreDirectory { get; }

    /// <summary>Where the shop data as it was before a restore is kept (never removed automatically).</summary>
    string BeforeRestoreDirectory { get; }
}
